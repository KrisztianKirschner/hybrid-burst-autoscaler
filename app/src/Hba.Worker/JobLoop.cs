using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.S3;
using Hba.Contracts;
using Hba.Processing;
using StackExchange.Redis;
using SixLabors.ImageSharp;

namespace Hba.Worker;


/// <summary>   
/// JobLoop.cs is the worker’s main background loop:
//      -Warms up the image pipeline, then repeatedly pulls one job from Redis. It backs off briefly when the queue is empty.
//      -Processes jobs sequentially: marks a job as processing, downloads its image, calls Hba.Processing, and uploads the result.
//      -Tracks progress with Redis heartbeats and Prometheus timings/counters.
//      -Handles failures: retries transient errors, marks terminal failures, and leaves jobs recoverable if it cannot update Redis.
//      -Finishes safely: removes completed jobs from the processing list; on shutdown, it stops taking new jobs and lets the current job finish
/// </summary>
public sealed class JobLoop(
    IConnectionMultiplexer redis,
    IJobStorage storage,
    IImagePipeline pipeline,
    WorkerMetrics metrics,
    WarmupGate warmup,
    WorkerOptions options,
    ILogger<JobLoop> logger) : BackgroundService
{
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metrics.SetWorkerInfo(options.Tier);
        await warmup.RunAsync(stoppingToken);
        logger.LogInformation(
            "Worker ready. Tier={Tier}, Node={Node}, ProcessorCount={ProcessorCount}",
            options.Tier,
            options.NodeName,
            Environment.ProcessorCount);

        var database = redis.GetDatabase();
        var pollDelay = InitialPollDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            RedisValue raw;
            try
            {
                raw = await database.ListMoveAsync(
                    RedisKeys.Jobs,
                    RedisKeys.Processing,
                    ListSide.Right,
                    ListSide.Left);
            }
            catch (Exception exception) when (RedisErrors.IsRedisFailure(exception))
            {
                logger.LogError(exception, "Failed to fetch a job from Redis.");
                await DelayAsync(pollDelay, stoppingToken);
                pollDelay = NextPollDelay(pollDelay);
                continue;
            }

            if (raw.IsNull)
            {
                await DelayAsync(pollDelay, stoppingToken);
                pollDelay = NextPollDelay(pollDelay);
                continue;
            }

            pollDelay = InitialPollDelay;
            try
            {
                await ProcessOneAsync(database, raw);
            }
            catch (Exception exception)
            {
                // Last resort: one job must never stop the worker (§10.4). If the message is still in
                // the processing list, the reaper returns it to the queue once its heartbeat is stale.
                logger.LogCritical(exception, "Unhandled error while handling a job; continuing with the next one.");
            }
        }
    }

    private async Task ProcessOneAsync(
        IDatabase database,
        RedisValue raw)
    {
        JobMessage message;
        try
        {
            message = JobMessage.Parse(raw.ToString());
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Discarding invalid job message from the processing list.");
            try
            {
                await RemoveFromProcessingAsync(database, raw);
            }
            catch (Exception removalException) when (RedisErrors.IsRedisFailure(removalException))
            {
                logger.LogError(removalException, "Could not remove invalid job message.");
            }

            return;
        }

        // An unchecked preset string must never become a label value (cardinality rule, §8.1).
        var presetLabel = PresetNames.IsKnown(message.Preset) ? message.Preset : "unknown";
        using var inProgress = metrics.TrackInProgress(options.Tier);

        var heartbeatCancellation = new CancellationTokenSource();
        var heartbeat = RunHeartbeatAsync(database, message.JobId, heartbeatCancellation.Token);
        var downloadSeconds = 0d;
        var processSeconds = 0d;
        var uploadSeconds = 0d;
        var startedAt = DateTimeOffset.UtcNow;
        var jobTimer = System.Diagnostics.Stopwatch.StartNew();
        var attempt = 0;

        try
        {
            if (message.SchemaVersion != JobMessage.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported job schema version {message.SchemaVersion}.");
            }

            if (!PresetNames.IsKnown(message.Preset))
            {
                throw new InvalidOperationException($"Unknown preset '{message.Preset}'.");
            }

            if (!Regex.IsMatch(message.InputKey, JobMessage.InputKeyPattern))
            {
                throw new InvalidOperationException(
                    $"Input key '{message.InputKey}' is outside the source set.");
            }

            attempt = await MarkProcessingAsync(database, message, startedAt);
            if (attempt == 1)
            {
                // Only the first attempt: a retry's wait would also include the earlier attempt.
                ObserveQueueWait(message, startedAt);
            }

            var input = await MeasureStageAsync(
                options.Tier,
                "download",
                () => storage.GetSourceAsync(message.InputKey, CancellationToken.None),
                seconds => downloadSeconds = seconds);

            ProcessedImage output;
            var processTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (metrics.TrackStage(options.Tier, "process"))
                {
                    output = pipeline.Process(input, message.Preset);
                }
            }
            catch (Exception exception) when (
                exception is InvalidImageContentException or
                    UnknownImageFormatException or
                    ArgumentException)
            {
                throw new InvalidOperationException(
                    "The source image or preset is invalid.",
                    exception);
            }
            finally
            {
                processTimer.Stop();
                processSeconds = processTimer.Elapsed.TotalSeconds;
            }

            var outputKey = $"out/{message.JobId:D}.{output.Extension}";
            await MeasureStageAsync(
                options.Tier,
                "upload",
                async () =>
                {
                    await storage.PutOutputAsync(
                        outputKey,
                        output.Data,
                        output.ContentType,
                        CancellationToken.None);
                    return true;
                },
                seconds => uploadSeconds = seconds);

            await CompleteAsync(
                database,
                raw,
                message,
                status: JobStatus.Done,
                DateTimeOffset.UtcNow,
                attempt,
                outputKey,
                options.Tier == WorkerOptions.TierAws ? OutputStores.S3 : OutputStores.Minio,
                error: null);
            // Only successful jobs: the metric has no status label, so failed or retried attempts
            // would skew the per-tier duration comparison.
            metrics.ObserveJobDuration(options.Tier, presetLabel, jobTimer.Elapsed.TotalSeconds);
            metrics.Completed(options.Tier, presetLabel, JobStatus.Done);
            LogJobSummary(
                message,
                JobStatus.Done,
                attempt,
                downloadSeconds,
                processSeconds,
                uploadSeconds);
        }
        catch (Exception exception)
        {
            try
            {
                if (IsTransient(exception) && attempt == 0)
                {
                    await RequeueAsync(database, raw, message);
                    logger.LogWarning(
                        exception,
                        "Returned job {JobId} to the queue because Redis failed before processing started.",
                        message.JobId);
                    return;
                }

                if (IsTransient(exception) && attempt > 0 && attempt < 3)
                {
                    var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                    await Task.Delay(delay, CancellationToken.None);
                    await RequeueAsync(database, raw, message);
                    logger.LogWarning(
                        exception,
                        "Job {JobId} will be retried after attempt {Attempt}.",
                        message.JobId,
                        attempt);
                    return;
                }

                if (attempt == 0)
                {
                    attempt = await MarkProcessingAsync(database, message, startedAt);
                }

                await CompleteAsync(
                    database,
                    raw,
                    message,
                    status: JobStatus.Failed,
                    DateTimeOffset.UtcNow,
                    attempt,
                    outputKey: null,
                    outputStore: null,
                    error: exception.Message);
                metrics.Completed(options.Tier, presetLabel, JobStatus.Failed);
                LogJobSummary(
                    message,
                    JobStatus.Failed,
                    attempt,
                    downloadSeconds,
                    processSeconds,
                    uploadSeconds);
                logger.LogError(exception, "Job {JobId} failed.", message.JobId);
            }
            catch (Exception completionException)
            {
                logger.LogError(
                    completionException,
                    "Could not record the outcome of job {JobId}; leaving it in the processing list for recovery.",
                    message.JobId);
            }
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }

            heartbeatCancellation.Dispose();
        }
    }

    private async Task<int> MarkProcessingAsync(
        IDatabase database,
        JobMessage message,
        DateTimeOffset startedAt)
    {
        var transaction = database.CreateTransaction();
        var statusTask = transaction.HashSetAsync(
            RedisKeys.Job(message.JobId),
            [
                new HashEntry(JobHash.Status, JobStatus.Processing),
                new HashEntry(JobHash.StartedAt, JobHash.FormatTimestamp(startedAt)),
                new HashEntry(JobHash.Worker, options.NodeName),
                new HashEntry(JobHash.Tier, options.Tier),
                new HashEntry(JobHash.HeartbeatAt, JobHash.FormatTimestamp(startedAt))
            ]);
        var expirationTask = transaction.KeyExpireAsync(
            RedisKeys.Job(message.JobId),
            JobHash.Ttl);
        var attemptsTask = transaction.HashIncrementAsync(
            RedisKeys.Job(message.JobId),
            JobHash.Attempts,
            1);
        if (!await transaction.ExecuteAsync())
        {
            throw new RedisException("Could not atomically mark the job as processing.");
        }

        await statusTask;
        await expirationTask;
        return (int)await attemptsTask;
    }

    private static async Task CompleteAsync(
        IDatabase database,
        RedisValue raw,
        JobMessage message,
        string status,
        DateTimeOffset finishedAt,
        int attempt,
        string? outputKey,
        string? outputStore,
        string? error)
    {
        const string script = $"""
            if redis.call('LREM', KEYS[2], 1, ARGV[1]) == 1 then
              redis.call('HSET', KEYS[1],
                '{JobHash.Status}', ARGV[2],
                '{JobHash.FinishedAt}', ARGV[3],
                '{JobHash.Attempts}', ARGV[4])
              if ARGV[5] == '' then
                redis.call('HDEL', KEYS[1], '{JobHash.Error}')
              else
                redis.call('HSET', KEYS[1], '{JobHash.Error}', ARGV[5])
              end
              if ARGV[6] ~= '' then
                redis.call('HSET', KEYS[1],
                  '{JobHash.OutputStore}', ARGV[7],
                  '{JobHash.OutputKey}', ARGV[6])
              end
              return 1
            end
            return 0
            """;

        var result = await database.ScriptEvaluateAsync(
            script,
            [RedisKeys.Job(message.JobId), RedisKeys.Processing],
            [
                raw,
                status,
                JobHash.FormatTimestamp(finishedAt),
                attempt,
                error ?? "",
                outputKey ?? "",
                outputStore ?? ""
            ]);

        if ((long)result != 1)
        {
            throw new RedisException(
                $"Job {message.JobId} was not in the processing list when finalizing.");
        }
    }

    private static async Task RequeueAsync(
        IDatabase database,
        RedisValue raw,
        JobMessage message)
    {
        const string script = $"""
            if redis.call('LREM', KEYS[1], 1, ARGV[1]) == 1 then
              redis.call('HSET', KEYS[3],
                '{JobHash.Status}', '{JobStatus.Queued}')
              redis.call('HDEL', KEYS[3], '{JobHash.Error}')
              redis.call('HDEL', KEYS[3],
                '{JobHash.StartedAt}', '{JobHash.FinishedAt}', '{JobHash.Worker}', '{JobHash.Tier}', '{JobHash.HeartbeatAt}',
                '{JobHash.OutputStore}', '{JobHash.OutputKey}')
              redis.call('RPUSH', KEYS[2], ARGV[1])
              return 1
            end
            return 0
            """;

        var result = await database.ScriptEvaluateAsync(
            script,
            [RedisKeys.Processing, RedisKeys.Jobs, RedisKeys.Job(message.JobId)],
            [raw]);
        if ((long)result != 1)
        {
            throw new RedisException(
                $"Job {message.JobId} could not be atomically returned to the queue.");
        }
    }

    private async Task RunHeartbeatAsync(
        IDatabase database,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await database.HashSetAsync(
                        RedisKeys.Job(jobId),
                        JobHash.HeartbeatAt,
                        JobHash.FormatTimestamp(DateTimeOffset.UtcNow));
                }
                catch (Exception exception) when (RedisErrors.IsRedisFailure(exception))
                {
                    logger.LogError(exception, "Heartbeat update failed for job {JobId}.", jobId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ObserveQueueWait(JobMessage message, DateTimeOffset startedAt)
    {
        var seconds = (startedAt - message.SubmittedAt).TotalSeconds;
        if (seconds < 0)
        {
            // The API's and this node's clocks disagree; the wait would otherwise be clamped silently.
            logger.LogWarning(
                "Negative queue wait of {Seconds:F3} s for job {JobId}: clock skew between the API host and node {Node}.",
                seconds,
                message.JobId,
                options.NodeName);
        }

        metrics.ObserveQueueWait(options.Tier, Math.Max(0, seconds));
    }

    private async Task RemoveFromProcessingAsync(IDatabase database, RedisValue raw)
    {
        var removed = await database.ListRemoveAsync(RedisKeys.Processing, raw, 1);
        if (removed != 1)
        {
            logger.LogWarning("Invalid job message was already removed from the processing list.");
        }
    }

    private void LogJobSummary(
        JobMessage message,
        string status,
        int attempt,
        double downloadSeconds,
        double processSeconds,
        double uploadSeconds) =>
        logger.LogInformation(
            "Job finished. JobId={JobId}, Preset={Preset}, Tier={Tier}, Status={Status}, Attempts={Attempts}, DownloadSeconds={DownloadSeconds}, ProcessSeconds={ProcessSeconds}, UploadSeconds={UploadSeconds}",
            message.JobId,
            message.Preset,
            options.Tier,
            status,
            attempt,
            downloadSeconds,
            processSeconds,
            uploadSeconds);

    private async Task<T> MeasureStageAsync<T>(
        string tier,
        string stage,
        Func<Task<T>> action,
        Action<double> recordDuration)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using (metrics.TrackStage(tier, stage))
            {
                return await action();
            }
        }
        finally
        {
            timer.Stop();
            recordDuration(timer.Elapsed.TotalSeconds);
        }
    }

    private static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RedisException or TimeoutException or HttpRequestException or IOException or TaskCanceledException)
            {
                return true;
            }

            if (current is AmazonS3Exception s3Exception &&
                (s3Exception.StatusCode == HttpStatusCode.RequestTimeout ||
                 (int)s3Exception.StatusCode == 429 ||
                 (int)s3Exception.StatusCode >= 500))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan NextPollDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(
            Math.Min(current.TotalMilliseconds * 2, options.IdlePollMaxMilliseconds));
}
