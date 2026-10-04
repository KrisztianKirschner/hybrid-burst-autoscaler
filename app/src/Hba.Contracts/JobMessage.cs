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

    public static JobMessage Parse(string json)
    {
        var message = JsonSerializer.Deserialize<JobMessage>(json)
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
