using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hba.Contracts;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Hba.Api.Controllers;

/// <summary>
/// Accepts jobs and reports their status (§5). Never processes images: the waiting queue is the
/// signal both autoscalers scale on, so work must go through it.
/// </summary>
[ApiController]
[Route("jobs")]
public sealed class JobsController(
    IJobQueue queue,
    TimeProvider timeProvider,
    ApiOptions options,
    ILogger<JobsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Submit(SubmitJobRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.InputKey is null || !Regex.IsMatch(request.InputKey, JobMessage.InputKeyPattern))
        {
            errors["input_key"] = [$"Must match {JobMessage.InputKeyPattern}."];
        }

        if (request.Preset is null || !PresetNames.IsKnown(request.Preset))
        {
            errors["preset"] = [$"Must be one of: {string.Join(", ", PresetNames.All)}."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        try
        {
            // Not atomic with the push, so the cap is approximate. It only guards Redis memory (§5.5).
            if (await queue.LengthAsync() >= options.MaxQueueLength)
            {
                Response.Headers.RetryAfter = "5";
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Queue is full",
                    detail: $"The queue already holds {options.MaxQueueLength} or more jobs.");
            }

            var message = JobMessage.Create(request.InputKey!, request.Preset!, timeProvider);
            await queue.EnqueueAsync(message);

            return Accepted(
                $"/jobs/{message.JobId:D}",
                new SubmitJobResponse(message.JobId, JobStatus.Queued));
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            return RedisUnavailable(exception);
        }
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId)
    {
        try
        {
            var fields = await queue.GetAsync(jobId);
            return fields is null
                ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown job")
                : Ok(JobStatusResponse.From(jobId, fields));
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            return RedisUnavailable(exception);
        }
    }

    private ObjectResult RedisUnavailable(Exception exception)
    {
        logger.LogError(exception, "Redis is unavailable.");
        return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Redis is unavailable");
    }
}

public sealed record SubmitJobRequest(
    [property: JsonPropertyName("input_key")] string? InputKey,
    [property: JsonPropertyName("preset")] string? Preset);

public sealed record SubmitJobResponse(
    [property: JsonPropertyName("job_id")] Guid JobId,
    [property: JsonPropertyName("status")] string Status);

/// <summary>The job's status hash as JSON (§5.3). Fields the job hasn't reached yet are null.</summary>
public sealed record JobStatusResponse(
    [property: JsonPropertyName("job_id")] Guid JobId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("preset")] string? Preset,
    [property: JsonPropertyName("input_key")] string? InputKey,
    [property: JsonPropertyName("output_store")] string? OutputStore,
    [property: JsonPropertyName("output_key")] string? OutputKey,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("worker")] string? Worker,
    [property: JsonPropertyName("attempts")] int? Attempts,
    [property: JsonPropertyName("submitted_at")] string? SubmittedAt,
    [property: JsonPropertyName("started_at")] string? StartedAt,
    [property: JsonPropertyName("finished_at")] string? FinishedAt,
    [property: JsonPropertyName("error")] string? Error)
{
    public static JobStatusResponse From(Guid jobId, IReadOnlyDictionary<string, string> fields)
    {
        string? Field(string name) => fields.GetValueOrDefault(name);

        return new JobStatusResponse(
            jobId,
            Field(JobHash.Status),
            Field(JobHash.Preset),
            Field(JobHash.InputKey),
            Field(JobHash.OutputStore),
            Field(JobHash.OutputKey),
            Field(JobHash.Tier),
            Field(JobHash.Worker),
            int.TryParse(Field(JobHash.Attempts), out var attempts) ? attempts : null,
            Field(JobHash.SubmittedAt),
            Field(JobHash.StartedAt),
            Field(JobHash.FinishedAt),
            Field(JobHash.Error));
    }
}
