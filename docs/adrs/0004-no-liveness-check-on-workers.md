---
status: accepted
date: 2026-10-13
decision-makers: Krisztián Kirschner
---

# No liveness check on the worker, on either orchestrator

## Context and Problem Statement

The worker image has a Docker `HEALTHCHECK` that runs `dotnet Hba.Worker.dll --healthcheck` every 30 s. Swarm executes it inside the container; Kubernetes ignores `HEALTHCHECK` and only runs the probes declared in the pod spec. The worker is limited to 1 CPU and its job duration is the measured quantity. Should the worker have a liveness check, and if so, how, without making the Swarm and Kubernetes runs unequal?

## Decision Drivers

* **Equal conditions on both orchestrators.** Any CPU the check uses on one side and not the other shows up as a difference in job duration that has nothing to do with the orchestrator.
* **The check competes with the job.** An `exec` check starts a second .NET process inside the same 1-CPU limit as the job.
* **Hung workers must not lose jobs.** A worker that runs but no longer works must not hold a job forever.

## Considered Options

* Keep the image's `HEALTHCHECK` on Swarm, no probe on Kubernetes (the default if nothing is done)
* The same `exec` check on both (Swarm `HEALTHCHECK`, Kubernetes `exec` livenessProbe)
* A cheap HTTP check on `/healthz/live` on both
* **No liveness check on either orchestrator**

## Decision Outcome

Chosen option: "No liveness check on either orchestrator". `stacks/swarm/hba.yml` sets `healthcheck: disable: true` for the worker; the Kubernetes manifests will declare no `livenessProbe`. The `HEALTHCHECK` stays in the Dockerfile for local use.

### Consequences

* Good, because both orchestrators run the worker under the same conditions, and no check takes CPU from the job.
* Good, because hung workers are still covered where it matters: the **reaper** puts a job back in the queue when its heartbeat is older than 60 s, so no job is lost. The `hba_` metrics and `/healthz/ready` make a stuck worker visible in Grafana.
* Bad, because neither orchestrator restarts a worker that is alive but stuck. It is only restarted when it exits or crashes. Acceptable for a few hours of load tests; not what a production setup would do.

## Pros and Cons of the Options

### Image `HEALTHCHECK` on Swarm only

* Bad, because Swarm runs an extra .NET process every 30 s inside the measured CPU limit and Kubernetes does not: the comparison is skewed.

### The same `exec` check on both

* Good, because it is fair and both orchestrators detect a stuck worker.
* Bad, because both sides pay the CPU cost, which adds noise to every job duration.

### HTTP check on `/healthz/live` on both

* Good, on Kubernetes: an `httpGet` probe is sent by the kubelet from outside, so it costs almost nothing in the container.
* Bad, on Swarm: it only supports `exec` checks, so it would need an HTTP client inside the image (the DHI runtime has no `curl`) and still start a process per check.

## More Information

The last option is a finding for the report: Kubernetes has native HTTP probes, Swarm only `exec` checks, so the same health check costs more on Swarm. See [docs/app.md](../app.md) §4.1 for how the other worker settings map between the two orchestrators.
