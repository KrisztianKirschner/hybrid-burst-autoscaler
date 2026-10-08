using System.Globalization;
using System.Text.Json;
using Hba.Contracts;
using StackExchange.Redis;

namespace Hba.Worker;


//summary
//JobReaper.cs is a background safety-net service:
//      -Every 30 seconds, it checks Redis’s processing list for jobs that workers have taken.
//      -For each job, it reads heartbeat_at from that job’s status record.
//      -If the heartbeat is older than 60 seconds, or missing on two passes in a row, it assumes the worker died and atomically moves the job back to the waiting queue.
//      -It updates the job status and logs recovered jobs. This lets another worker retry them instead of leaving them stuck.

public sealed class JobReaper(
    IConnectionMultiplexer redis,
    ILogger<JobReaper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    // Messages whose job had no heartbeat on the previous pass. Between LMOVE and the worker's
    // first HSET a freshly claimed job has no heartbeat for one round trip; reaping it then
    // would process it twice (more often on burst nodes, whose Redis round trip is longer).
    private HashSet<string> _missingHeartbeatLastPass = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReapStaleJobsAsync(redis.GetDatabase(), stoppingToken);
            }
            catch (Exception exception) when (RedisErrors.IsRedisFailure(exception))
            {
                logger.LogError(exception, "Redis job reaper pass failed.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A reaper bug must not stop the worker; the next pass tries again.
                logger.LogCritical(exception, "Unhandled error in job reaper pass.");
            }
        }
    }

    private async Task ReapStaleJobsAsync(IDatabase database, CancellationToken cancellationToken)
    {
        var messages = await database.ListRangeAsync(RedisKeys.Processing);
        var now = DateTimeOffset.UtcNow;
        var missingHeartbeatThisPass = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await ReapAsync(database, messages, now, missingHeartbeatThisPass, cancellationToken);
        }
        finally
        {
            _missingHeartbeatLastPass = missingHeartbeatThisPass;
        }
    }

    /// <summary>
    /// A job is stale if its heartbeat is older than <paramref name="staleAfter"/>, or if it has had
    /// no heartbeat on two consecutive passes. One missing observation is not enough: the worker may
    /// have claimed it a moment ago and not written its first heartbeat yet.
    /// </summary>
    public static bool IsStale(
        string? heartbeat,
        bool missingOnPreviousPass,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        if (DateTimeOffset.TryParse(heartbeat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var heartbeatAt))
        {
            return now - heartbeatAt > staleAfter;
        }

        return missingOnPreviousPass;
    }

    private async Task ReapAsync(
        IDatabase database,
        RedisValue[] messages,
        DateTimeOffset now,
        HashSet<string> missingHeartbeatThisPass,
        CancellationToken cancellationToken)
    {
        foreach (var raw in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            JobMessage message;
            try
            {
                message = JobMessage.Parse(raw.ToString());
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Invalid message found in the processing list.");
                var removed = await database.ListRemoveAsync(RedisKeys.Processing, raw, 1);
                if (removed == 0)
                {
                    logger.LogWarning("Invalid processing-list message was already removed.");
                }

                continue;
            }

            var heartbeat = await database.HashGetAsync(
                RedisKeys.Job(message.JobId),
                JobHash.HeartbeatAt);
            var key = raw.ToString();
            if (heartbeat.IsNullOrEmpty)
            {
                missingHeartbeatThisPass.Add(key);
            }

            if (!IsStale(heartbeat, _missingHeartbeatLastPass.Contains(key), now, StaleAfter))
            {
                continue;
            }

            if (await JobScripts.RequeueAsync(database, raw, message.JobId))
            {
                missingHeartbeatThisPass.Remove(key);
                logger.LogWarning(
                    "Returned stale job {JobId} to the queue after its worker heartbeat expired.",
                    message.JobId);
            }
        }
    }
}
