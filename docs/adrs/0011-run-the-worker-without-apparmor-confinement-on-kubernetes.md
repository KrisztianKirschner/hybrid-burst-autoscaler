---
status: accepted
date: 2026-11-17
decision-makers: Krisztián Kirschner
---

# Run the worker without AppArmor confinement on Kubernetes

## Context and Problem Statement

On the first deploy to Kubernetes (2026-11-17), worker pods died during warm-up with exit code 139: 5 of 7 pods. The same image, `hba-worker:sha-5de4c05`, never crashed on Swarm.

The kernel log showed the cause: an AppArmor denial of a `SIGABRT` that the .NET runtime's background GC thread (".NET BGC") sends within the process. On Ubuntu 26.04, containerd's default profile for Kubernetes pods (`cri-containerd.apparmor.d`) blocks it (peer label `…//&unconfined`), and the runtime then dies. Docker's `docker-default` profile, which Swarm uses, doesn't block it.

The worker's settings are frozen for the comparison: image, preset, CPU limit and the .NET runtime's behaviour must be the same on both orchestrators.

## Decision Drivers

* **The workload must not change.** Whatever fixes the crash must not change what the worker computes or how its runtime behaves, or the job times stop being comparable with Swarm's.
* **Least privilege**, as far as the first driver allows.
* **Time:** the Kubernetes measurements were due the same week.

## Considered Options

* **`appArmorProfile: Unconfined` for the worker container only**
* A local AppArmor profile that allows the signal (loaded on every node, burst nodes included)
* Change the .NET GC settings (for example turn background GC off) so the signal isn't sent
* Keep the default profile and let Kubernetes restart crashed pods

## Decision Outcome

**Chosen:** `securityContext.appArmorProfile.type: Unconfined` on the worker container (`stacks/k8s/hba.yml`), and on no other pod. Tested live before the change went to Git (PR #94): **0 of 12** workers crashed unconfined, against 5 of 7 under the default profile.

### Consequences

* Good, because it fixed the crash without touching the image or the runtime, so job times stay comparable with Swarm.
* Good, because it is one line in one container's spec, visible in review.
* Bad, because the worker is **less confined on Kubernetes than on Swarm** (no profile against `docker-default`). It still runs as the non-root user 65532 (`app/Dockerfile.worker`), with no service-account token and only its own Secret mounted, but this is a real security difference, and the report names it.
* Neutral, for the measurements: AppArmor checks some system calls, it doesn't compute. It is not expected to change job times, but this was not measured; Kubernetes jobs ran 2.5 % slower, with the cause still open ([loadtest.md](../loadtest.md) §4.7, finding 2).

## Pros and Cons of the Options

### A local profile that allows the signal

* Good, because the worker stays confined.
* Bad, because the profile must be written, loaded on every node and baked into the burst boot script, and a mistake in it shows up only as another crash. Too much for the week; the best follow-up.

### Change the .NET GC settings

* Good, because the profile stays.
* Bad, because it changes the runtime's memory behaviour on one orchestrator only, which is exactly what the comparison must not do.

### Let Kubernetes restart the pods

* Good, because nothing changes.
* Bad, because crashing workers lose warm-up time and jobs in flight; a run with restarts doesn't measure the orchestrator.

## More Information

**Revisit if** the project continues past the report: write a profile derived from `docker-default` for the worker, or check whether a newer containerd or Ubuntu release no longer denies the signal.

Related: [kubernetes.md](../kubernetes.md) §3 and §6, [ADR 0004](0004-no-liveness-check-on-workers.md) (the other worker setting chosen to keep the two orchestrators equal).
