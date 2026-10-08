using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hba.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Hba.Api.Tests;

public sealed class JobsEndpointTests
{
    private sealed class FakeJobQueue : IJobQueue
    {
        public long Length { get; set; }
        public bool Unavailable { get; set; }
        public List<JobMessage> Enqueued { get; } = [];
        public Dictionary<Guid, IReadOnlyDictionary<string, string>> Hashes { get; } = [];

        public Task<long> LengthAsync()
        {
            ThrowIfUnavailable();
            return Task.FromResult(Length);
        }

        public Task EnqueueAsync(JobMessage message)
        {
            ThrowIfUnavailable();
            Enqueued.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>?> GetAsync(Guid jobId)
        {
            ThrowIfUnavailable();
            return Task.FromResult(Hashes.GetValueOrDefault(jobId));
        }

        private void ThrowIfUnavailable()
        {
            if (Unavailable)
            {
                throw new RedisException("Redis is down (test).");
            }
        }
    }

    private static (HttpClient Client, FakeJobQueue Queue) CreateClient(int maxQueueLength = 50_000)
    {
        var queue = new FakeJobQueue();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IJobQueue>(queue);
                services.AddSingleton(new ApiOptions("redis:6379", maxQueueLength));
            }));
        return (factory.CreateClient(), queue);
    }

    [Fact]
    public async Task Post_ValidJob_Returns202WithLocationAndEnqueuesOneMessage()
    {
        var (client, queue) = CreateClient();

        var response = await client.PostAsJsonAsync("/jobs", new { input_key = "src/img-04.jpg", preset = "medium" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(queue.Enqueued);
        Assert.Equal("src/img-04.jpg", message.InputKey);
        Assert.Equal(PresetNames.Medium, message.Preset);
        Assert.Equal($"/jobs/{message.JobId:D}", response.Headers.Location?.OriginalString);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(message.JobId, body.GetProperty("job_id").GetGuid());
        Assert.Equal(JobStatus.Queued, body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("out/secret.webp", "medium", "input_key")]
    [InlineData("src/../x.jpg", "medium", "input_key")]
    [InlineData(null, "medium", "input_key")]
    [InlineData("src/img-04.jpg", "Medium", "preset")]
    [InlineData("src/img-04.jpg", null, "preset")]
    public async Task Post_InvalidJob_Returns400NamingTheField(string? inputKey, string? preset, string field)
    {
        var (client, queue) = CreateClient();

        var response = await client.PostAsJsonAsync("/jobs", new { input_key = inputKey, preset });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Post_FullQueue_Returns503WithRetryAfter()
    {
        var (client, queue) = CreateClient(maxQueueLength: 10);
        queue.Length = 10;

        var response = await client.PostAsJsonAsync("/jobs", new { input_key = "src/img-04.jpg", preset = "small" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Post_RedisDown_Returns503()
    {
        var (client, queue) = CreateClient();
        queue.Unavailable = true;

        var response = await client.PostAsJsonAsync("/jobs", new { input_key = "src/img-04.jpg", preset = "small" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownJob_Returns404()
    {
        var (client, _) = CreateClient();

        var response = await client.GetAsync($"/jobs/{Guid.CreateVersion7():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_KnownJob_ReturnsItsHashAsSnakeCaseJson()
    {
        var (client, queue) = CreateClient();
        var jobId = Guid.CreateVersion7();
        queue.Hashes[jobId] = new Dictionary<string, string>
        {
            [JobHash.Status] = JobStatus.Done,
            [JobHash.Preset] = PresetNames.Medium,
            [JobHash.InputKey] = "src/img-04.jpg",
            [JobHash.OutputStore] = OutputStores.RustFs,
            [JobHash.OutputKey] = $"out/{jobId:D}.webp",
            [JobHash.Attempts] = "1",
        };

        var body = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId:D}");

        Assert.Equal(jobId, body.GetProperty("job_id").GetGuid());
        Assert.Equal(JobStatus.Done, body.GetProperty("status").GetString());
        Assert.Equal(OutputStores.RustFs, body.GetProperty("output_store").GetString());
        Assert.Equal(1, body.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }
}
