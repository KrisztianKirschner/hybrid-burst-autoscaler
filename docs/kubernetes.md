# Kubernetes: the k3s Cluster, KEDA and the Stacks

**Status:** 2026-11-18. Live since 2026-11-17 and measured: three on-prem runs, three isolation runs and three burst runs ([loadtest.md](loadtest.md) §4.7–4.9). Built in milestones M1–M9 (PRs #90–#103). The switch back to Swarm has not been run yet ([cluster-vip.md](cluster-vip.md) §4).

**Audience:** anyone who knows the Swarm side of this project and wants to find, run, check or change its Kubernetes counterpart. Written for Kubernetes beginners: every part is mapped to the Swarm part it replaces.

**Related:** [ADR 0010](adrs/0010-k3s-as-a-second-vm-set-switched-by-active-cluster.md) (why k3s, and the two VM sets), [ADR 0011](adrs/0011-run-the-worker-without-apparmor-confinement-on-kubernetes.md) (AppArmor), [ADR 0012](adrs/0012-configure-keda-to-the-swarm-controllers-timing.md) (KEDA's timing), [burst.md](burst.md) §1.1 and §7.1 (bursting), [observability.md](observability.md) §5.1 (metrics), [app.md](app.md) §4.1 (the Swarm-to-Kubernetes checklist for the worker).

---

## 1. What runs where

Same shape as the Swarm cluster ([ADR 0006](adrs/0006-cluster-size-one-control-three-workers.md)): one control node and three workers, 2 vCPU / 4 GB each, and only one of the two clusters powered on at a time.

| VM | IP | Runs | Swarm counterpart |
|---|---|---|---|
| `k8s-ctl` (221) | 10.0.20.8 | k3s server (API, scheduler, controllers, SQLite), CoreDNS, `api`, `redis`, KEDA, kube-state-metrics, redis-exporter, the Prometheus agent, the burst controller | `swarm-ctl` |
| `k8s-01..03` (222–224) | 10.0.20.9, .10, .15 | k3s agent; two `worker` pods each | `swarm-01..03` |
| `burst-<instance-id>` (EC2, 0–2) | 10.100.x | k3s agent; two `worker` pods each | the same, joined with `docker swarm join` |

**Workers stay off the control node through a taint** (`node-role.kubernetes.io/control-plane:NoSchedule`, set by the `k3s` role), the Kubernetes way of Swarm's `node.role == worker`. Everything else that runs on `k8s-ctl` has a toleration for it and a `nodeSelector` that pins it there.

## 2. k3s, and what it bundles

k3s is a certified Kubernetes distribution in one binary: the server and the agent are the same program, and it brings its own containerd, flannel (pod network), CoreDNS, kube-proxy, a Helm controller and ServiceLB. That is why it was chosen over kubeadm, which leaves all of these to install and wire by hand ([ADR 0010](adrs/0010-k3s-as-a-second-vm-set-switched-by-active-cluster.md)).

The `k3s` role (`ansible/roles/k3s/`) installs it with the official script, pinned to **`v1.37.2+k3s1`**, and writes `/etc/rancher/k3s/config.yaml`:

| Setting | Value | Why |
|---|---|---|
| `token` | from the `K3S_TOKEN` secret (GitHub environment `proxmox`) | joins agents; burst nodes read the same token from SSM |
| `node-ip` | the VM's lab address | k3s would otherwise guess an interface |
| `server` (agents) | `https://10.0.20.8:6443` | agents join the control node directly; the VIP serves the app, not the API |
| `secrets-encryption` | `true` | Secrets encrypted on disk, as Swarm's raft store encrypts its secrets |
| `disable` | `traefik`, `metrics-server`, `local-storage` | no ingress on either cluster (same request path as Swarm); KEDA doesn't need metrics-server; Redis has no volume |
| `node-taint` | control-plane `NoSchedule` | §1 |
| `kube-controller-manager-arg` | `horizontal-pod-autoscaler-sync-period=5s`, `horizontal-pod-autoscaler-tolerance=0` | the HPA decides as often and as exactly as the Swarm controller ([ADR 0012](adrs/0012-configure-keda-to-the-swarm-controllers-timing.md)) |

A version bump in the role reinstalls on the next run (the tasks compare it with `k3s --version`). The burst user data pins the same version and must change with it.

## 3. The stacks (`stacks/k8s/`)

Plain YAML, applied by the `k8s_stacks` role on `k8s-ctl`. No `helm` CLI: the two third-party charts are `HelmChart` objects that k3s's built-in Helm controller installs.

| File | Contains | Swarm counterpart |
|---|---|---|
| `addons.yml` | KEDA 2.21.0 and kube-state-metrics 8.8.0 as `HelmChart`s | (none: the Swarm controller is our own code) |
| `hba.yml` | namespace `hba`; `api` (Service type `LoadBalancer` on :8080) and `redis` (strategy `Recreate`); `worker` Deployment | `stacks/swarm/hba.yml` |
| `monitoring.yml` | redis-exporter and the Prometheus agent with its ServiceAccount | `stacks/swarm/monitoring.yml` |
| `prometheus-agent.yml` | the agent's config (becomes a ConfigMap): scrape jobs `redis`, `kube-state-metrics`, `burst-controller`, `worker` | the Swarm agent's config |
| `keda.yml` | the `ScaledObject` (§4) | `autoscaler-policy.yml` + the controller |
| `burst.yml` | the burst controller: ServiceAccount and RBAC, Service :8000, Deployment | the burst controller's Swarm service |
| `burst-policy.yml` | the burst policy (becomes a ConfigMap) | `stacks/swarm/burst-policy.yml` |

**The worker Deployment, setting by setting** (the checklist is [app.md](app.md) §4.1): no `replicas` field (KEDA owns it, and a re-apply leaves the live count alone, where `docker stack deploy` resets it); CPU request and limit `1`, so a 2-vCPU node holds two and a third stays `Pending`; no probes ([ADR 0004](adrs/0004-no-liveness-check-on-workers.md)); `terminationGracePeriodSeconds: 30`; the node name through the Downward API (`spec.nodeName`), so `burst-*` nodes write to S3; the RustFS keys as a Secret mounted at `/run/secrets`, the same paths as Swarm secrets; `automountServiceAccountToken: false`; `appArmorProfile: Unconfined` ([ADR 0011](adrs/0011-run-the-worker-without-apparmor-confinement-on-kubernetes.md)).

**The role's order:** the namespace; the Secrets `rustfs` and `burst-aws` from environment variables (never in Git); the ConfigMaps; the add-ons; a wait until KEDA's `scaledobjects.keda.sh` type exists; then `hba`, `monitoring`, `keda`, `burst`; finally `kubectl rollout restart` for each Deployment whose ConfigMap changed. The stack files are read on the Pi with a `file` lookup, because `kubernetes.core.k8s`'s `src:` reads on the target, where the repo isn't (fixed in PR #93).

## 4. Autoscaling: KEDA and the HPA

```
Redis list hba:jobs ──► KEDA (reads LLEN) ──► HPA keda-hpa-worker ──► worker Deployment replicas
```

KEDA does not scale anything itself: it creates an HPA and feeds it the queue length as an external metric. The HPA then computes `ceil(queue / 5)`. Each Swarm controller setting has a counterpart:

| Swarm controller (`autoscaler-policy.yml`) | Kubernetes (`keda.yml`, `k3s` role) |
|---|---|
| `poll_interval: 5` | `pollingInterval: 5`, HPA sync period 5 s |
| `jobs_per_replica: 5` | Redis trigger `listLength: "5"` |
| `min_replicas: 1` / `max_replicas: 10` (6 on-prem) | `minReplicaCount: 1` / `maxReplicaCount: 10` (6 for on-prem reruns) |
| scale straight to the target | `scaleUp`: up to 10 pods per 15 s, no stabilization |
| `scale_down_stabilization: 300` | `scaleDown`: 300 s window, then 100 % at once |
| exact target, no dead band | HPA tolerance 0 (default ±10 %) |
| reads the queue from Prometheus (4.4–7.9 s old) | reads Redis directly (current) |

The last row is the one difference left on purpose. Three extra runs showed what it is worth ([loadtest.md](loadtest.md) §4.8).

## 5. Checking it

On `k8s-ctl` (SSH as `proxima`), `kubectl` is built into k3s:

```sh
sudo k3s kubectl get nodes -o wide                    # 4 Ready (more while bursting), versions, IPs
sudo k3s kubectl -n hba get pods -o wide              # which pod runs on which node
sudo k3s kubectl -n hba get scaledobject,hpa          # KEDA's object and the HPA it drives
sudo k3s kubectl -n hba describe hpa keda-hpa-worker  # current queue metric, desired replicas, events
sudo k3s kubectl -n hba exec deploy/redis -- redis-cli LLEN hba:jobs   # the queue itself
sudo k3s kubectl describe node k8s-01 | grep -A6 'Allocated resources' # CPU requested: 2 workers = 2000m
sudo k3s kubectl -n hba get pods --field-selector=status.phase=Pending # workers waiting for a node
sudo k3s kubectl -n hba logs deploy/burst-controller -f                # burst decisions
sudo k3s kubectl -n kube-system get helmchart         # KEDA and kube-state-metrics installs
sudo k3s secrets-encrypt status                       # Encryption Status: Enabled
journalctl -u k3s -f                                  # the server's log (k3s-agent on workers)
```

**Before a comparison run:** 4 nodes Ready; 1 worker pod; queue 0; `kubectl describe node` shows 0 CPU requested on the workers apart from the worker pods. CoreDNS (100m) must stay on `k8s-ctl`: on a worker it would leave room for only one 1-CPU worker pod there.

## 6. Problems found while building it

- **AppArmor killed the worker** (exit 139 during warm-up, 5 of 7 pods): containerd's default profile blocks a signal the .NET runtime sends to itself. Found through the AppArmor denial in the kernel log, fixed with `Unconfined` ([ADR 0011](adrs/0011-run-the-worker-without-apparmor-confinement-on-kubernetes.md)).
- **`src:` in `kubernetes.core.k8s` is read on the target** (§3), so the first deploy failed.
- **New VMs were unreachable from the CI Pi** until they had their return routes, which Ansible itself installs: solved on `ts-gateway` ([network.md](network.md) §2.3).

## 7. Known gaps

- The switch back to Swarm (`active_cluster = "swarm"`) has not been run since the Kubernetes set exists.
- `app.yml`'s image bumps change only the Swarm stack file; the Kubernetes one has to be bumped by hand (the burst controller's bump already covers both).
- One k3s server on SQLite: no control-plane redundancy, as on Swarm with one manager.

## 8. Sources

- k3s: [configuration file](https://docs.k3s.io/installation/configuration), [Helm controller](https://docs.k3s.io/helm), [networking services (ServiceLB)](https://docs.k3s.io/networking/networking-services), [secrets encryption](https://docs.k3s.io/security/secrets-encryption)
- KEDA: [ScaledObject spec](https://keda.sh/docs/latest/reference/scaledobject-spec/), [Redis lists scaler](https://keda.sh/docs/latest/scalers/redis-lists/)
- Kubernetes: [Horizontal Pod Autoscaling](https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/) (sync period, tolerance, behavior), [taints and tolerations](https://kubernetes.io/docs/concepts/scheduling-eviction/taint-and-toleration/), [AppArmor](https://kubernetes.io/docs/tutorials/security/apparmor/)
