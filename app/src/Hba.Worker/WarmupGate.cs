using Hba.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Hba.Worker;

//summary
// WarmupGate.cs prepares the image pipeline before the worker starts taking jobs:
//      -Creates a small sample image in memory.
//      -Runs the medium preset over it the configured number of times, defaulting to three.
//      -Records the warm-up duration as a Prometheus metric.
//      -Marks warm-up complete so the readiness endpoint can report the worker as ready

public sealed class WarmupGate
{
    private readonly ImagePipeline _pipeline;
    private readonly WorkerMetrics _metrics;
    private readonly string _tier;
    private readonly int _iterations;
    private volatile bool _isComplete;

    public WarmupGate(ImagePipeline pipeline, WorkerMetrics metrics, WorkerOptions options)
    {
        _pipeline = pipeline;
        _metrics = metrics;
        _tier = options.Tier;
        _iterations = options.WarmupIterations;
    }

    public bool IsComplete => _isComplete;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sample = CreateSample();
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _pipeline.Process(sample, "medium");
        }

        stopwatch.Stop();
        _metrics.SetWarmupDuration(_tier, stopwatch.Elapsed.TotalSeconds);
        _isComplete = true;
        return Task.CompletedTask;
    }

    private static byte[] CreateSample()
    {
        using var image = new Image<Rgb24>(1024, 1024, new Rgb24(96, 128, 160));
        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 85 });
        return output.ToArray();
    }
}
