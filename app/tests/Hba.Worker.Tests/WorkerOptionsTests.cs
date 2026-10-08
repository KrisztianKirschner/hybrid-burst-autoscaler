using Hba.Worker;

namespace Hba.Worker.Tests;

public sealed class WorkerOptionsTests
{
    private static Dictionary<string, string?> ValidOnPrem() => new()
    {
        ["HBA_TIER"] = "onprem",
        ["HBA_S3_BUCKET"] = "hba",
        ["HBA_S3_ENDPOINT_ONPREM"] = "http://rustfs:9000",
        ["HBA_S3_ACCESS_KEY"] = "access-key-value",
        ["HBA_S3_SECRET_KEY"] = "secret-key-value",
    };

    private static WorkerOptions Load(Dictionary<string, string?> variables) =>
        WorkerOptions.FromEnvironment(name => variables.GetValueOrDefault(name));

    [Fact]
    public void ValidOnPremConfiguration_Loads()
    {
        var options = Load(ValidOnPrem());

        Assert.Equal(WorkerOptions.TierOnPrem, options.Tier);
        Assert.Equal("secret-key-value", options.OnPremSecretKey);
    }

    [Fact]
    public void Secret_CanBeReadFromFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "secret-from-file\n");
            var variables = ValidOnPrem();
            variables.Remove("HBA_S3_SECRET_KEY");
            variables["HBA_S3_SECRET_KEY_FILE"] = path;

            var options = Load(variables);

            Assert.Equal("secret-from-file", options.OnPremSecretKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Secret_RejectsBothValueAndFile()
    {
        var variables = ValidOnPrem();
        variables["HBA_S3_SECRET_KEY_FILE"] = "/run/secrets/s3_secret_key";

        Assert.Throws<InvalidOperationException>(() => Load(variables));
    }

    [Fact]
    public void Secret_FileThatCannotBeReadFailsAtStartup()
    {
        var variables = ValidOnPrem();
        variables.Remove("HBA_S3_SECRET_KEY");
        variables["HBA_S3_SECRET_KEY_FILE"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidOperationException>(() => Load(variables));
    }

    [Theory]
    [InlineData("eu-central1")]
    [InlineData("not-a-region")]
    [InlineData("")]
    public void Region_RejectsUnknownNames(string region)
    {
        var variables = ValidOnPrem();
        variables["HBA_S3_REGION"] = region;

        Assert.Throws<InvalidOperationException>(() => Load(variables));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30")]
    [InlineData("45")]
    public void ShutdownTimeout_MustStayBelowGracePeriod(string seconds)
    {
        var variables = ValidOnPrem();
        variables["HBA_SHUTDOWN_TIMEOUT_SECONDS"] = seconds;

        Assert.Throws<InvalidOperationException>(() => Load(variables));
    }

    [Fact]
    public void ToString_DoesNotLeakSecrets()
    {
        var variables = ValidOnPrem();
        variables["HBA_REDIS"] = "redis:6379,password=redis-password-value";

        var text = Load(variables).ToString();

        Assert.DoesNotContain("access-key-value", text);
        Assert.DoesNotContain("secret-key-value", text);
        Assert.DoesNotContain("redis-password-value", text);
        Assert.Contains("Bucket = hba", text);
    }
}
