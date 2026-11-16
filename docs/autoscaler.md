# Swarm Autoscaler Controller

**Status:** 2026-10-16. Live on the lab swarm since PR #37: the controller runs on `swarm-ctl`, reads the queue length from `obs-01` and reports its metrics (`up{job="autoscaler"} = 1`). A scale-up under real load on the lab passed on 2026-10-16: 500 jobs took it from 1 to 6 replicas (2 on each of `swarm-01..03`) and back to 1 (§8). Before that it was tested only on a local single-node swarm.

**Audience:** anyone changing the scaling policy, deploying or debugging the controller, or comparing it with KEDA.

**Related:** [docs/app.md](app.md) §4 (the worker service it scales), [docs/observability.md](observability.md) §5 (where the queue length comes from), [ADR 0005](adrs/0005-in-cluster-prometheus-agent-with-remote-write.md) (why it reads Prometheus).

---

## 1. What it does

Swarm has no autoscaler of its own, so this project has a small Python controller. Kubernetes uses KEDA for the same job; the contrast between the two is one of the comparison points.

Every 5 s the controller:

1. reads the queue length from Prometheus on `obs-01` (`redis_key_size{key="hba:jobs"}`);
2. reads the current replica count of `hba_worker` from the Docker API;
3. computes `desired = ceil(queue_length / jobs_per_replica)`, limited to `min_replicas … max_replicas`;
4. scales up to `desired` once the scale-up cooldown has passed, and scales down only to the highest `desired` of the last `scale_down_stabilization` seconds (§4), then sets the replica count. Swarm then starts or stops worker tasks.

It never touches jobs or workers directly. It changes one number, and Swarm does the rest.

```
API → Redis hba:jobs → redis-exporter → agent → Prometheus (obs-01)
                                                      │ every 5 s
                                                      ▼
                        Docker API ◄── controller (manager node)
                            │
                            ▼
                 hba_worker: N replicas ──► workers pull jobs from Redis
```

## 2. Files

| File | Contents |
|---|---|
| `autoscaler/swarm/controller.py` | the loop |
| `autoscaler/swarm/Dockerfile` | image on Docker Hardened Images (§6) |
| `autoscaler/swarm/requirements.txt` | `docker`, `requests`, `prometheus-client`, `pyyaml`, exact versions |
| `.github/workflows/autoscaler.yml` | builds the image; pushes `ghcr.io/krisztiankirschner/hba-autoscaler:sha-<commit>` on `main` |
| `stacks/swarm/autoscaler.yml` | the `autoscaler` stack: service `controller`, pinned to the manager |
| `stacks/swarm/autoscaler-policy.yml` | the policy (§3), mounted as a Swarm config |
| `stacks/swarm/prometheus-agent.yml` | scrape job `autoscaler` on `autoscaler_controller:8000` |

The policy lives in `stacks/swarm/`, not next to the code, because the `stacks` role only copies that folder to the manager. It is deployment config, like the agent config.

## 3. The policy

| Field | Value | Meaning |
|---|---|---|
| `service` | `hba_worker` | the Swarm service to scale |
| `prometheus_url` | `http://10.0.20.30:9090` | Prometheus on `obs-01` |
| `query` | `redis_key_size{key="hba:jobs"}` | the queue length |
| `jobs_per_replica` | 5 | queued jobs one replica is expected to handle; tune after preset calibration ([app.md](app.md) §5) |
| `min_replicas` | 1 | never fewer, even with an empty queue |
| `max_replicas` | 6 | 3 worker nodes (`swarm-01..03`) × 2 replicas ([ADR 0006](adrs/0006-cluster-size-one-control-three-workers.md)); raise with every worker node added, and for bursting |
| `poll_interval` | 5 | seconds between iterations |
| `scale_up_cooldown` | 15 | seconds since the last scale-up |
| `scale_down_stabilization` | 300 | a scale-down goes only to the highest `desired` seen in this many seconds; the same as the Kubernetes HPA's default scale-down stabilization window |
| `scale_down_cooldown` | 60 | **unused** since 2026-10-24: kept in the file only so the old controller image could still start; remove it in a clean-up PR |

Times are plain numbers in seconds. `5s` would crash the loop.

**`max_replicas` must match the cluster.** Each worker reserves 1 CPU, so a 2-vCPU node holds 2. A higher `max_replicas` than the nodes can hold leaves tasks `Pending`. That is the burst controller's trigger ([burst.md](burst.md)), but on-prem only it just means replicas that never start.

**Changing the policy:** edit the file and open a PR. The `stacks` role names the Swarm config after a hash of the file (`AUTOSCALER_POLICY_VERSION`), so a changed file becomes a new config, and Swarm restarts the controller with it. Old configs stay behind (`docker config ls`); remove them now and then.

## 4. Decision rules

| Queue length | `ceil(n / 5)` | After the limits |
|---|---|---|
| 0 | 0 | 1 |
| 4 | 1 | 1 |
| 7 | 2 | 2 |
| 40 | 8 | 6 (the on-prem plateau) |

- **Scale up quickly, scale down slowly.** Up: at most every 15 s, straight to `desired`. Down: only to the **highest** `desired` of the last 300 s, so the queue has to stay low for the whole window before any replica is removed. A 60 s cooldown was replaced because it let the replicas oscillate in the load test's warm-up, 6 → 1 → 6 ([loadtest.md](loadtest.md) §4); the window keeps them at 6 until the work is really finished. The price is five more minutes at full size after every run.
- **A restarted controller has no history.** Its window starts empty, so it corrects a wrong replica count at once, in either direction, and a restart during a run can remove replicas the window would have held.
- **No queue length, no change.** If Prometheus doesn't answer, or the series is missing, the controller keeps the current replicas and logs a warning. "No data" is never treated as an empty queue, which would scale down to `min`.
- **A refused scale is retried.** If the service changed between reading and scaling (Docker answers "update out of sequence"), the controller logs it and tries again in the next iteration.
- **Shutdown is clean.** On SIGTERM it finishes the current iteration and exits with code 0.

## 5. Metrics

Served on `:8000/metrics`, scraped by the in-swarm agent as job `autoscaler`:

| Metric | Meaning |
|---|---|
| `hba_autoscaler_queue_depth` | the queue length it last read |
| `hba_autoscaler_current_replicas` | replicas Swarm reported |
| `hba_autoscaler_desired_replicas` | replicas it wants |
| `hba_autoscaler_scalings_total{direction}` | scale operations sent, `up` / `down` |
| `hba_autoscaler_errors_total{source}` | iterations skipped, `prometheus` / `docker` |

Every decision is also logged: `docker service logs autoscaler_controller`.

## 6. Image and deployment

**Two stages on Docker Hardened Images**, the same policy as the app images. The build stage (`dhi.io/python:3.14-alpine3.24-dev`) installs the dependencies into a virtualenv. The runtime stage (`dhi.io/python:3.14-alpine3.24`) gets only the virtualenv and `controller.py`: no shell, no pip, 74 MB. Both stages are pinned by digest and must stay the same Python release (3.14.8 today), or the copied virtualenv may not run. Bump them together.

**Runs as `user: "0"`.** It needs the manager's Docker socket, which belongs to root. DHI images run as UID 65532 by default and have **no `root` entry in `/etc/passwd`**, so `user: root` fails with "unable to find user root". The numeric form works. Anything with the Docker socket is effectively root on the manager; name it in the limitations.

**Publishing a new version:**

1. A change under `autoscaler/swarm/` is merged; CI pushes `sha-<merge commit>` to GHCR (public, so the swarm pulls without credentials).
2. A second PR changes the tag in `stacks/swarm/autoscaler.yml`; merging it deploys the new image.

The stack currently runs `sha-04785ec`. `sha-9b40aec` (same code, build stage pinned by digest) is published but not deployed yet.

## 7. Known issues

- **Every deploy resets the worker count.** `docker stack deploy` sets `hba_worker` back to the `replicas: 1` in `hba.yml`, overriding the controller. The controller corrects it in its next iteration, within 5 s, and its first scaling after the reset has no cooldown. Don't deploy during a test run.
- **Data age.** The controller sees the queue length after the agent's 5 s scrape, the remote-write delay and its own 5 s loop: roughly 5–15 s old. KEDA reads Redis directly. Match the effective intervals before the comparison runs ([observability.md](observability.md) §8).
- **It only scales on queue length.** It doesn't look at CPU or job duration. `Pending` tasks are the burst controller's job ([burst.md](burst.md)), not this one's.

## 8. Testing

**On a local single-node swarm** (Docker Desktop), without touching the lab:

1. Copy `stacks/swarm/` to a scratch folder. In the copy, point the agent's `remote_write` at an unused address (`http://127.0.0.1:1/api/v1/write`), so no test data reaches `obs-01`.
2. `docker swarm init`, create dummy `rustfs_access_key` / `rustfs_secret_key` secrets, deploy `hba`, `monitoring` and `autoscaler` in that order.
3. Let the worker run on the manager: `docker service update --constraint-rm 'node.role == worker' hba_worker`.
4. The controller reads the lab's real queue (read-only) and scales the local worker. Raise it by hand (`docker service scale hba_worker=2`); with an empty queue it should go back to 1 within one iteration.
5. Clean up: `docker stack rm autoscaler monitoring hba`, remove the secrets, `docker swarm leave --force`.

**On the lab, after a deploy:**

1. `docker service logs autoscaler_controller` on `swarm-ctl`: `depth=… current=… desired=…` every 5 s.
2. On `obs-01`: `up{job="autoscaler"}` is 1.
3. Submit a batch of jobs through the API, large enough not to drain within seconds: use the `medium` preset (target 2–3 s per job, not calibrated yet), or 500 jobs with `small` (about 0.7 s each; 100 drain before the scale-up is visible). `hba_autoscaler_desired_replicas` and `current_replicas` should rise within about 15 s, and fall back to 1 about 60 s after the last scale-up. Watch it on the run dashboard ([observability.md](observability.md) §6).

```bash
for i in $(seq 1 500); do
  curl -s -o /dev/null -w '%{http_code}\n' -X POST http://api.hba.internal:8080/jobs \
    -H 'Content-Type: application/json' \
    -d '{"input_key":"src/img-01.jpg","preset":"small"}'
done | sort | uniq -c        # expect: 500 202
```

**First lab test, 2026-10-16.** 500 `small` jobs, submitted 07:01:12–07:01:50 UTC (about 13 jobs/s, sequential curl over Tailscale). Prometheus, 10 s steps:

| UTC | Queue | Desired | Scheduled | Ready | Busy |
|---|---|---|---|---|---|
| 07:01:10 | 0 | 1 | 1 | 1 | 0 |
| 07:01:20 | 32 | 1 | 1 | 1 | 1 |
| 07:01:30 | 99 | 6 | 1 | 3 | 1 |
| 07:01:40 | 213 | 6 | 6 | 6 | 6 |
| 07:01:50 | 256 | 6 | 6 | 6 | 6 |
| 07:02:00 | 115 | 6 | 6 | 6 | 6 |
| 07:02:10 | 0 | 1 | 6 | 6 | 0 |
| 07:02:40 | 0 | 1 | 1 | 1 | 0 |

- About 20–30 s from the first job to full capacity: one or two controller loops, then about 10 s until the new replicas were warmed up and scraped.
- Placement as planned: 2 replicas on each of `swarm-01..03`, none on `swarm-ctl`.
- 500 done, 0 failed.
- Desired fell to 1 as soon as the queue was empty; Swarm kept 6 for another 30 s. That was the 60 s scale-down cooldown, counted from the scale-up (replaced by the 300 s stabilization window on 2026-10-24, §4).
- The plateau lasted only about 30 s, because `small` jobs are too short. It is a functional test, not a result. The first run under k6 load with the calibrated `medium` preset is in [loadtest.md](loadtest.md) §4 (6 replicas reached after 46 s, a plateau of about 8 minutes).

## 9. Bursting

Bursting is **not** part of this controller. An earlier version of this section planned it as one more step in this loop. On 2026-10-30 it became a separate burst controller instead ([burst.md](burst.md), [ADR 0009](adrs/0009-separate-burst-controller-resizing-an-auto-scaling-group.md)). That split mirrors KEDA vs the Cluster Autoscaler, and it lets both orchestrators burst with the same code.

The only change here is `max_replicas`: 6 for on-prem-only runs, and 10 for burst runs (6 on-prem + 2 burst nodes × 2). The extra replicas stay `Pending` until the burst nodes join, and that `Pending` state is what the burst controller reacts to.

## 10. Sources

- [Docker: scale a service](https://docs.docker.com/engine/swarm/services/#scale-a-service), [Swarm configs](https://docs.docker.com/engine/swarm/configs/)
- [Docker SDK for Python: services](https://docker-py.readthedocs.io/en/stable/services.html)
- [Prometheus HTTP API: instant queries](https://prometheus.io/docs/prometheus/latest/querying/api/#instant-queries)
- [prometheus_client: HTTP exposition](https://prometheus.github.io/client_python/exporting/http/)
- [Docker Hardened Images: Python](https://hub.docker.com/hardened-images/catalog/dhi/python)
