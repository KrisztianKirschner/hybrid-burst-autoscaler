namespace Hba.Contracts;

/// <summary>
/// Field names of the per-job status hash at <see cref="RedisKeys.Job"/>. The API creates the
/// hash and reads it for GET /jobs/{id}; the worker and the reaper update it.
/// </summary>
public static class JobHash
{
    public const string Status = "status";
    public const string InputKey = "input_key";
    public const string Preset = "preset";
    public const string SubmittedAt = "submitted_at";
    public const string Attempts = "attempts";
    public const string StartedAt = "started_at";
    public const string Worker = "worker";
    public const string Tier = "tier";
    public const string HeartbeatAt = "heartbeat_at";
    public const string FinishedAt = "finished_at";
    public const string OutputStore = "output_store";
    public const string OutputKey = "output_key";
    public const string Error = "error";

    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");
}
