# hba-worker

`hba-worker` is a single-job-at-a-time .NET worker. It moves messages from
`hba:jobs` to `hba:jobs:processing`, reads source images from the object store
selected for its tier, runs `Hba.Processing`, and writes the output under
`out/{job_id}.{extension}`.

## Configuration

| Environment variable | Required | Default | Purpose |
|---|---:|---|---|
| `HBA_REDIS` | no | `redis:6379` | StackExchange.Redis connection string |
| `HBA_TIER` | no | `auto` | `onprem`, `aws`, or infer from the node name |
| `HBA_NODE_NAME` | for `auto` | none | Names beginning with `burst-` resolve to `aws`; other names resolve to `onprem` |
| `HBA_S3_BUCKET` | yes | none | Bucket name shared by the selected object store |
| `HBA_S3_ENDPOINT_ONPREM` | on-prem only | none | MinIO URL, for example `http://minio:9000` |
| `HBA_S3_ACCESS_KEY` | on-prem only | none | MinIO access key; supply as an orchestrator secret |
| `HBA_S3_SECRET_KEY` | on-prem only | none | MinIO secret key; supply as an orchestrator secret |
| `HBA_S3_REGION` | no | `eu-central-1` | AWS region; AWS credentials come from the default SDK credential chain |
| `HBA_IDLE_POLL_MAX_MS` | no | `500` | Maximum delay while the queue is empty |
| `HBA_WARMUP_ITERATIONS` | no | `3` | Medium-preset warm-up runs; `0` disables warm-up |
| `HBA_SHUTDOWN_TIMEOUT_SECONDS` | no | `25` | .NET host graceful-shutdown timeout |

For local on-prem development, set the MinIO endpoint and credentials, bucket,
and either set `HBA_TIER=onprem` or provide `HBA_NODE_NAME`. For AWS, set
`HBA_TIER=aws` (or inject a `burst-` node name) and use an instance profile; do
not configure static AWS keys.

The service listens on port `8080`. `GET /healthz/live` checks that the process
responds, `GET /healthz/ready` also requires completed warm-up and a successful
Redis ping, and `GET /metrics` exposes Prometheus metrics.

Build and test the solution from the repository root:

```powershell
dotnet test app/hba.slnx
dotnet publish app/src/Hba.Worker/Hba.Worker.csproj -c Release
```

Build the Linux worker image from the `app` directory with
`docker build -f Dockerfile.worker -t hba-worker .`.

<!-- worker.md documents how to configure, run, and publish the worker: -->
<!-- Lists the worker’s environment variables, required settings, and defaults.
     Explains how tier selection chooses MinIO for on-prem workers and S3 for AWS workers.
D    escribes the worker’s health and metrics endpoints.
     Provides commands to test, publish, and build the Docker image -->