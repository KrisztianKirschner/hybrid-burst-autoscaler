using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hba.Contracts;

public sealed record JobMessage(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("job_id")] Guid JobId,
    [property: JsonPropertyName("input_key")] string InputKey,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset SubmittedAt)
{
    public const int CurrentSchemaVersion = 1;

    // Source objects a job may read; anything else in the bucket is off limits.
    public const string InputKeyPattern = @"^src/[a-z0-9-]+\.(jpg|jpeg|png)$";

    // Shared by ToJson and Parse so the API and the worker agree on the wire format.
    // Missing or null fields throw JsonException instead of becoming default values.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static JobMessage Create(string inputKey, string preset, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new JobMessage(
            CurrentSchemaVersion,
            Guid.CreateVersion7(now),
            inputKey,
            preset,
            now);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static JobMessage Parse(string json)
    {
        var message = JsonSerializer.Deserialize<JobMessage>(json, JsonOptions)
            ?? throw new JsonException("Job message was JSON null.");

        if (message.JobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(message.InputKey) ||
            string.IsNullOrWhiteSpace(message.Preset))
        {
            throw new JsonException("Job message is missing a required field.");
        }

        return message;
    }
}
