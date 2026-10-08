using Hba.Contracts;
using Prometheus;
using System.Runtime.InteropServices;

namespace Hba.Worker;

//summary
// WorkerMetrics.cs defines and updates the worker’s Prometheus metrics:
//      -Counts completed and failed jobs, labelled by tier, preset, and status.
//      -Measures total job duration, time spent in each stage (download, process, upload), and queue wait.
//      -Tracks whether a replica is processing a job and how long warm-up takes.
//      -Publishes worker/runtime information—such as CPU instruction support, vector width, and processor count—to help compare on-prem and AWS workers

public sealed class WorkerMetrics
{
    private static readonly double[] Buckets =
    [
        0.1, 0.25, 0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 15, 30
    ];

    private static readonly double[] QueueWaitBuckets =
    [
        0.1, 0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300, 600
    ];

    private readonly Counter _completed = Metrics.CreateCounter(
        "hba_jobs_completed_total",
        "Jobs reaching a terminal state.",
        new CounterConfiguration { LabelNames = ["tier", "preset", "status"] });

    private readonly Histogram _jobDuration = Metrics.CreateHistogram(
        "hba_job_duration_seconds",
        "Time spent on a successful job, from start through finish. Failed and retried attempts are not observed.",
        new HistogramConfiguration
        {
            LabelNames = ["tier", "preset"],
            Buckets = Buckets
        });

    private readonly Histogram _stageDuration = Metrics.CreateHistogram(
        "hba_job_stage_seconds",
        "Time spent in a worker job stage.",
        new HistogramConfiguration
        {
            LabelNames = ["tier", "stage"],
            Buckets = Buckets
        });

    private readonly Histogram _queueWait = Metrics.CreateHistogram(
        "hba_job_queue_wait_seconds",
        "Time a job waited in the queue before processing.",
        new HistogramConfiguration
        {
            LabelNames = ["tier"],
            Buckets = QueueWaitBuckets
        });

    private readonly Gauge _inProgress = Metrics.CreateGauge(
        "hba_jobs_in_progress",
        "Whether this worker replica is processing a job.",
        new GaugeConfiguration { LabelNames = ["tier"] });

    private readonly Gauge _warmupDuration = Metrics.CreateGauge(
        "hba_worker_warmup_seconds",
        "Time taken by worker warm-up.",
        new GaugeConfiguration { LabelNames = ["tier"] });

    private readonly Gauge _workerInfo = Metrics.CreateGauge(
        "hba_worker_info",
        "Worker runtime and CPU capabilities.",
        new GaugeConfiguration
        {
            LabelNames =
            [
                "tier", "version", "avx2", "avx512", "vector_bits", "processor_count"
            ]
        });

    public void SetWarmupDuration(string tier, double seconds) =>
        _warmupDuration.WithLabels(tier).Set(seconds);

    public void SetWorkerInfo(string tier)
    {
        _workerInfo.WithLabels(
            tier,
            RuntimeInformation.FrameworkDescription,
            System.Runtime.Intrinsics.X86.Avx2.IsSupported.ToString().ToLowerInvariant(),
            System.Runtime.Intrinsics.X86.Avx512F.IsSupported.ToString().ToLowerInvariant(),
            (System.Numerics.Vector<byte>.Count * 8).ToString(),
            Environment.ProcessorCount.ToString()).Set(1);
    }

    public IDisposable TrackInProgress(string tier) =>
        _inProgress.WithLabels(tier).TrackInProgress();

    public void ObserveJobDuration(string tier, string preset, double seconds) =>
        _jobDuration.WithLabels(tier, NormalizePreset(preset)).Observe(seconds);

    public void ObserveQueueWait(string tier, double seconds) =>
        _queueWait.WithLabels(tier).Observe(seconds);

    public IDisposable TrackStage(string tier, string stage) =>
        _stageDuration.WithLabels(tier, stage).NewTimer();

    public void Completed(string tier, string preset, string status) =>
        _completed.WithLabels(tier, NormalizePreset(preset), status).Inc();

    private static string NormalizePreset(string preset) =>
        PresetNames.IsKnown(preset) ? preset : "unknown";
}
