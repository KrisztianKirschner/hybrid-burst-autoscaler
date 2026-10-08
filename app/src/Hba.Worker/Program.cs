using Hba.Processing;
using Prometheus;
using StackExchange.Redis;

namespace Hba.Worker;

//summary
//Program.cs sets up and starts the worker service:
//      -Handles --healthcheck by checking whether the worker’s local liveness endpoint responds.
//      -Reads and validates worker configuration, then registers Redis, storage, image processing, metrics, and background services with dependency injection.
//      -Starts the HTTP server on port 8080 and exposes liveness, readiness, and Prometheus metrics endpoints.
//      -Sets the graceful-shutdown timeout and JSON logging

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--healthcheck", StringComparer.Ordinal))
        {
            return await RunHealthcheckAsync();
        }

        WorkerOptions options;
        try
        {
            options = WorkerOptions.FromEnvironment();
        }
        catch (InvalidOperationException exception)
        {
            // Logging isn't set up yet: one clear line instead of an unhandled-exception stack trace.
            await Console.Error.WriteLineAsync($"Invalid worker configuration: {exception.Message}");
            return 1;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:8080");
        builder.Services.Configure<HostOptions>(host =>
            host.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        // Otherwise every Prometheus scrape and health probe logs two Information lines (§8.5).
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisOptions = ConfigurationOptions.Parse(options.Redis);
            redisOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisOptions);
        });
        builder.Services.AddSingleton<IJobStorage, S3JobStorage>();
        builder.Services.AddSingleton<IImagePipeline, ImagePipeline>();
        builder.Services.AddSingleton<WorkerMetrics>();
        builder.Services.AddSingleton<WarmupGate>();
        builder.Services.AddHostedService<JobLoop>();
        builder.Services.AddHostedService<JobReaper>();

        var app = builder.Build();
        app.UseHttpMetrics();
        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));
        app.MapGet(
            "/healthz/ready",
            async (WarmupGate warmup, IConnectionMultiplexer connection) =>
            {
                if (!warmup.IsComplete)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    await connection.GetDatabase().PingAsync();
                    return Results.Ok(new { status = "ready" });
                }
                catch (RedisException)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            });
        app.MapMetrics();
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> RunHealthcheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync("http://localhost:8080/healthz/live");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
