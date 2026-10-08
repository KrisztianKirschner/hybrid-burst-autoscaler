using Hba.Contracts;
using StackExchange.Redis;

namespace Hba.Worker;

/// <summary>
/// Redis Lua scripts shared by JobLoop and JobReaper. Each runs atomically inside Redis, so a
/// message is never in two lists, or in none, between steps.
/// </summary>
internal static class JobScripts
{
    // KEYS[1] = processing list, KEYS[2] = jobs queue, KEYS[3] = job hash; ARGV[1] = exact message bytes.
    // RPUSH puts the job on the side workers pop from: it has already waited the longest (§6.5).
    private static readonly string RequeueScript = $"""
        if redis.call('LREM', KEYS[1], 1, ARGV[1]) == 1 then
          redis.call('RPUSH', KEYS[2], ARGV[1])
          redis.call('HSET', KEYS[3], '{JobHash.Status}', '{JobStatus.Queued}')
          redis.call('HDEL', KEYS[3],
            '{JobHash.StartedAt}', '{JobHash.FinishedAt}', '{JobHash.Worker}', '{JobHash.Tier}', '{JobHash.HeartbeatAt}',
            '{JobHash.OutputStore}', '{JobHash.OutputKey}', '{JobHash.Error}')
          redis.call('EXPIRE', KEYS[3], {(long)JobHash.Ttl.TotalSeconds})
          return 1
        end
        return 0
        """;

    /// <summary>
    /// Moves the message from the processing list back to the front of the queue.
    /// Returns false if it was no longer in the processing list (another worker or reaper got there first).
    /// </summary>
    public static async Task<bool> RequeueAsync(IDatabase database, RedisValue raw, Guid jobId)
    {
        var result = await database.ScriptEvaluateAsync(
            RequeueScript,
            [RedisKeys.Processing, RedisKeys.Jobs, RedisKeys.Job(jobId)],
            [raw]);
        return (long)result == 1;
    }
}
