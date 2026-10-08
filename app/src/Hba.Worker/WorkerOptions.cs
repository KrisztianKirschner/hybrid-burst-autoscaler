using System.Globalization;
using System.Text;
using StackExchange.Redis;

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

    // The orchestrators' stop grace period (Swarm stop_grace_period, Kubernetes
    // terminationGracePeriodSeconds) is 30 s; the host must finish before SIGKILL (§10.3).
    private const int OrchestratorGracePeriodSeconds = 30;

    public static WorkerOptions FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable);

    public static WorkerOptions FromEnvironment(Func<string, string?> getVariable)
    {
        var tierSetting = Get(getVariable, "HBA_TIER", "auto").ToLowerInvariant();
        var configuredNodeName = getVariable("HBA_NODE_NAME");
        var nodeName = string.IsNullOrWhiteSpace(configuredNodeName)
            ? Environment.MachineName
            : configuredNodeName;
        var tier = ResolveTier(tierSetting, configuredNodeName);
        var options = new WorkerOptions(
            Redis: Get(getVariable, "HBA_REDIS", "redis:6379"),
            Tier: tier,
            NodeName: nodeName,
            Bucket: GetRequired(getVariable, "HBA_S3_BUCKET"),
            OnPremEndpoint: Get(getVariable, "HBA_S3_ENDPOINT_ONPREM", ""),
            OnPremAccessKey: GetSecret(getVariable, "HBA_S3_ACCESS_KEY"),
            OnPremSecretKey: GetSecret(getVariable, "HBA_S3_SECRET_KEY"),
            AwsRegion: Get(getVariable, "HBA_S3_REGION", "eu-central-1"),
            IdlePollMaxMilliseconds: GetInt(getVariable, "HBA_IDLE_POLL_MAX_MS", 500),
            WarmupIterations: GetInt(getVariable, "HBA_WARMUP_ITERATIONS", 3),
            ShutdownTimeoutSeconds: GetInt(getVariable, "HBA_SHUTDOWN_TIMEOUT_SECONDS", 25));
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

        try
        {
            _ = ConfigurationOptions.Parse(Redis);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("HBA_REDIS is not a valid Redis configuration string.", exception);
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

        if (ShutdownTimeoutSeconds <= 0 || ShutdownTimeoutSeconds >= OrchestratorGracePeriodSeconds)
        {
            throw new InvalidOperationException(
                $"HBA_SHUTDOWN_TIMEOUT_SECONDS must be between 1 and {OrchestratorGracePeriodSeconds - 1}, " +
                $"below the orchestrator's {OrchestratorGracePeriodSeconds} s grace period.");
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

        // GetBySystemName never throws: an unknown name comes back as a region called "Unknown",
        // so a typo would only surface at the first S3 call on a burst node. Check the known list instead.
        if (!Amazon.RegionEndpoint.EnumerableAllRegions.Any(region =>
                string.Equals(region.SystemName, AwsRegion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"HBA_S3_REGION '{AwsRegion}' is not a known AWS region.");
        }
    }

    // Records print every property by default; keep the MinIO keys and any Redis password out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Redis = {RedactRedis(Redis)}, ");
        builder.Append($"Tier = {Tier}, NodeName = {NodeName}, Bucket = {Bucket}, ");
        builder.Append($"OnPremEndpoint = {OnPremEndpoint}, ");
        builder.Append($"OnPremAccessKey = {Redact(OnPremAccessKey)}, ");
        builder.Append($"OnPremSecretKey = {Redact(OnPremSecretKey)}, ");
        builder.Append($"AwsRegion = {AwsRegion}, IdlePollMaxMilliseconds = {IdlePollMaxMilliseconds}, ");
        builder.Append($"WarmupIterations = {WarmupIterations}, ShutdownTimeoutSeconds = {ShutdownTimeoutSeconds}");
        return true;
    }

    private static string Redact(string? value) =>
        string.IsNullOrEmpty(value) ? "(unset)" : "***";

    private static string RedactRedis(string redis)
    {
        try
        {
            return ConfigurationOptions.Parse(redis).ToString(includePassword: false);
        }
        catch (ArgumentException)
        {
            return "***";
        }
    }

    private static string Get(Func<string, string?> getVariable, string name, string fallback) =>
        getVariable(name) ?? fallback;

    private static string GetRequired(Func<string, string?> getVariable, string name)
    {
        var value = getVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value;
    }

    // Swarm delivers secrets only as files under /run/secrets, never as environment variables,
    // so every secret can also be given as NAME_FILE (the convention used by official images).
    private static string GetSecret(Func<string, string?> getVariable, string name)
    {
        var value = getVariable(name);
        var path = getVariable($"{name}_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return value ?? "";
        }

        if (!string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Set either {name} or {name}_FILE, not both.");
        }

        try
        {
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{name}_FILE '{path}' could not be read.", exception);
        }
    }

    private static int GetInt(Func<string, string?> getVariable, string name, int fallback)
    {
        var value = getVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"{name} must be an integer.");
    }
}
