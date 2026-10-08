using Hba.Contracts;
using StackExchange.Redis;

namespace Hba.Api;

/// <summary>The API's only access to Redis (§6). An interface so controller tests can use a fake.</summary>
public interface IJobQueue
{
    /// <summary>Number of jobs waiting in <see cref="RedisKeys.Jobs"/>.</summary>
    Task<long> LengthAsync();

    /// <summary>Creates the job's status hash and pushes its message, in one transaction (§6.3).</summary>
    Task EnqueueAsync(JobMessage message);

    /// <summary>The job's status hash, or null if it doesn't exist or has expired.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetAsync(Guid jobId);
}

public sealed class RedisJobQueue(IConnectionMultiplexer redis) : IJobQueue
{
    public async Task<long> LengthAsync() =>
        await redis.GetDatabase().ListLengthAsync(RedisKeys.Jobs);

    public async Task EnqueueAsync(JobMessage message)
    {
        var key = RedisKeys.Job(message.JobId);
        var transaction = redis.GetDatabase().CreateTransaction();

        // The hash is written before the message is pushed, in one MULTI/EXEC: otherwise a fast
        // worker could take a job whose hash doesn't exist yet (§6.3).
        var hashTask = transaction.HashSetAsync(
            key,
            [
                new HashEntry(JobHash.Status, JobStatus.Queued),
                new HashEntry(JobHash.InputKey, message.InputKey),
                new HashEntry(JobHash.Preset, message.Preset),
                new HashEntry(JobHash.SubmittedAt, JobHash.FormatTimestamp(message.SubmittedAt)),
                new HashEntry(JobHash.Attempts, 0)
            ]);
        var expireTask = transaction.KeyExpireAsync(key, JobHash.Ttl);
        var pushTask = transaction.ListLeftPushAsync(RedisKeys.Jobs, message.ToJson());

        if (!await transaction.ExecuteAsync())
        {
            throw new RedisException($"Could not enqueue job {message.JobId}.");
        }

        await Task.WhenAll(hashTask, expireTask, pushTask);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(Guid jobId)
    {
        var entries = await redis.GetDatabase().HashGetAllAsync(RedisKeys.Job(jobId));
        return entries.Length == 0
            ? null
            : entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());
    }
}
