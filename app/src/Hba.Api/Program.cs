using Hba.Api;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var options = ApiOptions.FromConfiguration(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisOptions = ConfigurationOptions.Parse(options.Redis);
    redisOptions.AbortOnConnectFail = false;   // start even if Redis isn't up yet; reconnect in the background
    return ConnectionMultiplexer.Connect(redisOptions);
});
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();              // /openapi/v1.json
    app.MapScalarApiReference();   // /scalar
}

app.MapControllers();

// §8.4: liveness never touches Redis, so a Redis restart can't make the orchestrator restart the API.
app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/healthz/ready", async (IConnectionMultiplexer redis) =>
{
    try
    {
        await redis.GetDatabase().PingAsync();
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

// Lets the test project start the app with WebApplicationFactory<Program>.
public partial class Program;
