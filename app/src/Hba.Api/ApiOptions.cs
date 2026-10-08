using System.Globalization;

namespace Hba.Api;

/// <summary>The API's HBA_* settings (§9.1).</summary>
public sealed record ApiOptions(string Redis, int MaxQueueLength)
{
    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var redis = configuration["HBA_REDIS"] is { Length: > 0 } value ? value : "redis:6379";
        var maxQueueText = configuration["HBA_MAX_QUEUE_LENGTH"];
        var maxQueueLength = 50_000;
        if (!string.IsNullOrWhiteSpace(maxQueueText) &&
            (!int.TryParse(maxQueueText, NumberStyles.None, CultureInfo.InvariantCulture, out maxQueueLength) ||
             maxQueueLength <= 0))
        {
            throw new InvalidOperationException("HBA_MAX_QUEUE_LENGTH must be a positive integer.");
        }

        return new ApiOptions(redis, maxQueueLength);
    }
}
