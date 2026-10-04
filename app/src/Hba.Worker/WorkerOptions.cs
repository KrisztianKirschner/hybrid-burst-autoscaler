using System.Globalization;

namespace Hba.Worker;

//summary
// WorkerOptions.cs loads and validates the worker’s environment-based configuration:
//      -Reads Redis, storage, tier, polling, warm-up, and shutdown settings, applying defaults where allowed.
//      -Resolves the worker tier from HBA_TIER, or in auto mode from HBA_NODE_NAME (burst-... means AWS).
//      -Requires the bucket and, for on-prem workers, a valid MinIO endpoint and credentials.
//      -Fails early with a clear error if configuration is missing or invalid


public sealed record WorkerOptions(
    string Redis,
    string Tier,
    string NodeName,
    string Bucket,
    string? OnPremEndpoint,
    string? OnPremAccessKey,
    string? OnPremSecretKey,
    string AwsRegion,
    int IdlePollMaxMilliseconds,
    int WarmupIterations,
    int ShutdownTimeoutSeconds)
{
    public const string TierOnPrem = "onprem";
    public const string TierAws = "aws";

    public static WorkerOptions FromEnvironment()
    {
        var tierSetting = Get("HBA_TIER", "auto").ToLowerInvariant();
        var configuredNodeName = Environment.GetEnvironmentVariable("HBA_NODE_NAME");
        var nodeName = string.IsNullOrWhiteSpace(configuredNodeName)
            ? Environment.MachineName
            : configuredNodeName;
        var tier = ResolveTier(tierSetting, configuredNodeName);
        var options = new WorkerOptions(
            Redis: Get("HBA_REDIS", "redis:6379"),
            Tier: tier,
            NodeName: nodeName,
            Bucket: GetRequired("HBA_S3_BUCKET"),
            OnPremEndpoint: Get("HBA_S3_ENDPOINT_ONPREM", ""),
            OnPremAccessKey: Get("HBA_S3_ACCESS_KEY", ""),
            OnPremSecretKey: Get("HBA_S3_SECRET_KEY", ""),
            AwsRegion: Get("HBA_S3_REGION", "eu-central-1"),
            IdlePollMaxMilliseconds: GetInt("HBA_IDLE_POLL_MAX_MS", 500),
            WarmupIterations: GetInt("HBA_WARMUP_ITERATIONS", 3),
            ShutdownTimeoutSeconds: GetInt("HBA_SHUTDOWN_TIMEOUT_SECONDS", 25));
        options.Validate();
        return options;
    }

    public static string ResolveTier(string tier, string? nodeName)
    {
        var normalizedTier = tier.Trim().ToLowerInvariant();
        if (normalizedTier == TierOnPrem || normalizedTier == TierAws)
        {
            return normalizedTier;
        }

        if (normalizedTier != "auto")
        {
            throw new InvalidOperationException(
                $"HBA_TIER must be 'auto', '{TierOnPrem}', or '{TierAws}'.");
        }

        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new InvalidOperationException(
                "HBA_NODE_NAME is required when HBA_TIER=auto.");
        }

        return nodeName.StartsWith("burst-", StringComparison.OrdinalIgnoreCase)
            ? TierAws
            : TierOnPrem;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Redis))
        {
            throw new InvalidOperationException("HBA_REDIS cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Bucket))
        {
            throw new InvalidOperationException("HBA_S3_BUCKET is required.");
        }

        if (IdlePollMaxMilliseconds < 50)
        {
            throw new InvalidOperationException("HBA_IDLE_POLL_MAX_MS must be at least 50.");
        }

        if (WarmupIterations < 0)
        {
            throw new InvalidOperationException("HBA_WARMUP_ITERATIONS cannot be negative.");
        }

        if (ShutdownTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("HBA_SHUTDOWN_TIMEOUT_SECONDS must be positive.");
        }

        if (Tier == TierOnPrem)
        {
            if (!Uri.TryCreate(OnPremEndpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException(
                    "HBA_S3_ENDPOINT_ONPREM must be an absolute URL for on-prem workers.");
            }

            if (string.IsNullOrWhiteSpace(OnPremAccessKey) ||
                string.IsNullOrWhiteSpace(OnPremSecretKey))
            {
                throw new InvalidOperationException(
                    "HBA_S3_ACCESS_KEY and HBA_S3_SECRET_KEY are required for on-prem workers.");
            }
        }
        else if (!string.Equals(Tier, TierAws, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported worker tier '{Tier}'.");
        }

        try
        {
            _ = Amazon.RegionEndpoint.GetBySystemName(AwsRegion);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("HBA_S3_REGION is invalid.", exception);
        }
    }

    private static string Get(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) ?? fallback;

    private static string GetRequired(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value;
    }

    private static int GetInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"{name} must be an integer.");
    }
}
