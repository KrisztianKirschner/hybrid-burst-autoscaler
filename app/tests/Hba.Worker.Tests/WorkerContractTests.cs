using Hba.Contracts;
using Hba.Worker;

namespace Hba.Worker.Tests;

public sealed class WorkerContractTests
{
    [Theory]
    [InlineData("burst-w3", WorkerOptions.TierAws)]
    [InlineData("swarm-w1", WorkerOptions.TierOnPrem)]
    public void ResolveTier_AutoUsesNodeName(string nodeName, string expectedTier)
    {
        Assert.Equal(expectedTier, WorkerOptions.ResolveTier("auto", nodeName));
    }

    [Theory]
    [InlineData(WorkerOptions.TierAws)]
    [InlineData(WorkerOptions.TierOnPrem)]
    public void ResolveTier_ExplicitValueOverridesNodeName(string tier)
    {
        Assert.Equal(tier, WorkerOptions.ResolveTier(tier, "burst-w3"));
    }

    [Fact]
    public void ResolveTier_AutoRequiresNodeName()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkerOptions.ResolveTier("auto", null));

        Assert.Contains("HBA_NODE_NAME", exception.Message);
    }

    [Fact]
    public void ResolveTier_RejectsUnknownTier()
    {
        Assert.Throws<InvalidOperationException>(
            () => WorkerOptions.ResolveTier("unknown", "worker-1"));
    }

    [Fact]
    public void Parse_DeserializesSnakeCaseQueueMessage()
    {
        var jobId = Guid.CreateVersion7();
        var submittedAt = DateTimeOffset.Parse("2026-10-12T14:03:11.402Z");
        var json =
            $$"""{"schema_version":1,"job_id":"{{jobId:D}}","input_key":"src/img-04.jpg","preset":"medium","submitted_at":"{{submittedAt:O}}"}""";

        var message = JobMessage.Parse(json);

        Assert.Equal(JobMessage.CurrentSchemaVersion, message.SchemaVersion);
        Assert.Equal(jobId, message.JobId);
        Assert.Equal("src/img-04.jpg", message.InputKey);
        Assert.Equal("medium", message.Preset);
        Assert.Equal(submittedAt, message.SubmittedAt);
    }

    [Fact]
    public void ToJson_ThenParse_ReturnsEqualMessage()
    {
        var original = JobMessage.Create("src/img-04.jpg", "medium", TimeProvider.System);

        Assert.Equal(original, JobMessage.Parse(original.ToJson()));
    }

    [Fact]
    public void Create_UsesUuidV7AndUtc()
    {
        var message = JobMessage.Create("src/img-04.jpg", "medium", TimeProvider.System);

        Assert.Equal(JobMessage.CurrentSchemaVersion, message.SchemaVersion);
        Assert.Equal(7, message.JobId.Version);
        Assert.Equal(TimeSpan.Zero, message.SubmittedAt.Offset);
    }

    [Theory]
    [InlineData("""{"schema_version":1,"job_id":"0192f3c4-8a1e-7cc0-b1f2-3d4e5f607182","input_key":"src/img-04.jpg","preset":"medium"}""")]
    [InlineData("""{"job_id":"0192f3c4-8a1e-7cc0-b1f2-3d4e5f607182","input_key":"src/img-04.jpg","preset":"medium","submitted_at":"2026-10-12T14:03:11.402Z"}""")]
    [InlineData("""{"schema_version":1,"job_id":"0192f3c4-8a1e-7cc0-b1f2-3d4e5f607182","input_key":null,"preset":"medium","submitted_at":"2026-10-12T14:03:11.402Z"}""")]
    public void Parse_RejectsMissingOrNullFields(string json)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => JobMessage.Parse(json));
    }

    [Fact]
    public void RedisKeys_AgreeWithQueueContract()
    {
        var jobId = Guid.Parse("0192f3c4-8a1e-7cc0-b1f2-3d4e5f607182");

        Assert.Equal("hba:jobs", RedisKeys.Jobs);
        Assert.Equal("hba:jobs:processing", RedisKeys.Processing);
        Assert.Equal($"hba:job:{jobId:D}", RedisKeys.Job(jobId));
    }
}
