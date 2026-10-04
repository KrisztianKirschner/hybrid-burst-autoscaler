using System.Text.Json;
using Hba.Contracts;
using StackExchange.Redis;

namespace Hba.Worker;


//summary
//JobReaper.cs is a background safety-net service:
//      -Every 30 seconds, it checks Redis’s processing list for jobs that workers have taken.
//      -For each job, it reads heartbeat_at from that job’s status record.
//      -If the heartbeat is missing or older than 60 seconds, it assumes the worker died and atomically moves the job back to the waiting queue.
//      -It updates the job status and logs recovered jobs. This lets another worker retry them instead of leaving them stuck.

public sealed class JobReaper(
    IConnectionMultiplexer redis,
    ILogger<JobReaper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private const string RequeueStaleScript = """
        if redis.call('LREM', KEYS[1], 1, ARGV[1]) == 1 then
          redis.call('RPUSH', KEYS[2], ARGV[1])
          redis.call('HSET', KEYS[3], 'status', 'queued')
          redis.call('HDEL', KEYS[3],
            'started_at', 'finished_at', 'worker', 'tier', 'heartbeat_at',
            'output_store', 'output_key', 'error')
          redis.call('EXPIRE', KEYS[3], 86400)
          return 1
        end
        return 0
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReapStaleJobsAsync(redis.GetDatabase(), stoppingToken);
            }
            catch (RedisException exception)
            {
                logger.LogError(exception, "Redis job reaper pass failed.");
            }
        }
    }

    private async Task ReapStaleJobsAsync(IDatabase database, CancellationToken cancellationToken)
    {
        var messages = await database.ListRangeAsync(RedisKeys.Processing);
        var now = DateTimeOffset.UtcNow;

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
                "heartbeat_at");
            var isStale = !DateTimeOffset.TryParse(
                heartbeat.ToString(),
                out var heartbeatAt) ||
                now - heartbeatAt > StaleAfter;
            if (!isStale)
            {
                continue;
            }

            var result = await database.ScriptEvaluateAsync(
                RequeueStaleScript,
                [
                    RedisKeys.Processing,
                    RedisKeys.Jobs,
                    RedisKeys.Job(message.JobId)
                ],
                [raw]);
            if ((long)result == 1)
            {
                logger.LogWarning(
                    "Returned stale job {JobId} to the queue after its worker heartbeat expired.",
                    message.JobId);
            }
        }
    }
}
