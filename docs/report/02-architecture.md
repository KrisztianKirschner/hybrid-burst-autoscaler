# 2. Architecture & IaC Design

## 2.1 System Context

## 2.2 Deployment Architecture

Each cluster consists of **1 control node and 3 workers** on-prem. EC2 burst workers join the same cluster as extra workers, from 0 up to a cost cap. The control node runs the manager or control plane together with `api`, `redis`, the ingress and the autoscaler, and **runs no job replicas**. Worker nodes run only job replicas. Every worker, on-prem or in the cloud, has 2 vCPU and 4 GB, the same as `c6a.large`, so a worker node provides the same capacity in either tier.

**On-prem (Proxmox, cloned from template 9999).** Network `10.0.20.0/24`, gateway OPNsense at `10.0.20.1`, VMID = 200 + last IP octet.

| VMID | Name | IP | Role | vCPU | RAM | Disk | Runs job replicas? | Powered on |
|---|---|---|---|---|---|---|---|---|
| 9999 | `ubuntu-2604-template` | — | template | 2 | 4 GB | 20 GB | — | never |
| 211 | `swarm-ctl` | 10.0.20.11 | Swarm manager, `api`, `redis`, ingress, autoscaler | 2 | 4 GB | 30 GB | no | Swarm tests |
| 212 | `swarm-01` | 10.0.20.12 | Swarm worker | 2 | 4 GB | 30 GB | yes | Swarm tests |
| 213 | `swarm-02` | 10.0.20.13 | Swarm worker | 2 | 4 GB | 30 GB | yes | Swarm tests |
| 214 | `swarm-03` | 10.0.20.14 | Swarm worker | 2 | 4 GB | 30 GB | yes | Swarm tests |
| 221 | `k8s-ctl` | 10.0.20.21 | K8s control plane, `api`, `redis`, ingress, KEDA | 2 | 4 GB | 30 GB | no | K8s tests |
| 222 | `k8s-01` | 10.0.20.22 | K8s worker | 2 | 4 GB | 30 GB | yes | K8s tests |
| 223 | `k8s-02` | 10.0.20.23 | K8s worker | 2 | 4 GB | 30 GB | yes | K8s tests |
| 224 | `k8s-03` | 10.0.20.24 | K8s worker | 2 | 4 GB | 30 GB | yes | K8s tests |
| 230 | `obs-01` | 10.0.20.30 | Prometheus, Grafana (MinIO: to be decided) | 2 | 4 GB | 50 GB | — | always |

*Built so far: `swarm-ctl`, `swarm-01` and `obs-01`. The other VMs are added to the same Terraform map as the project progresses. The ingress (Traefik) is not deployed yet; until it is, `api` publishes port 8080 through Swarm's routing mesh.*

**Cloud (AWS, on demand).**

| Name | Type | Role | vCPU | RAM | Count |
|---|---|---|---|---|---|
| `burst-wN` | `c6a.large` | worker, joins the active cluster over Tailscale | 2 | 4 GiB | 0 → N (capped for cost) |

The load generator is not in the cloud: k6 runs from the author's laptop on the tailnet, outside the cluster. An isolated AWS instance was planned and dropped after the first pilot showed the laptop is far from its limit ([ADR 0008](../adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)).

**Host budget** (Ryzen 7 5700G, 16 threads / 32 GB).

| Scenario | vCPU | RAM |
|---|---|---|
| During a test: one cluster (4 VMs) + `obs-01` + OPNsense | 12 / 16 | 22 / 32 GB |
| Both clusters on together (never done) | 20 / 16 | 38 / 32 GB (exceeds RAM) |
| Disk promised (thin-provisioned `local-lvm`, incl. the 20 GB template; clones are full copies) | — | 310 GB of ~760 GB free |

On-prem capacity is expected to level off at about **6 worker replicas** (3 workers × 2 vCPU). Load beyond that is handled by bursting to EC2.

## 2.3 Infrastructure as Code

### 2.3.w Terraform and Ansible: who owns what

The rule is simple: **Terraform decides what exists, Ansible decides what runs on it.**

- **Terraform** creates the on-prem VMs from one map (`local.vms`: name, VMID, IP, labels) through one reusable module, all cloned from template 9999. There are two separate root configs, `terraform/proxmox` and `terraform/aws`. Each has its own state file in one S3 bucket with native S3 locking, because they have different credentials and different lifecycles. The AWS root holds only the always-on scaffolding (VPC, subnet, security group, S3). Burst instances are planned to be launched by a script rather than by an apply per scale event.
- **Ansible** configures the VMs in four roles, run in order: `common` (base packages), `docker` (Docker engine), `swarm` (`swarm init` on the control node, `swarm join` on the workers), `stacks` (deploys the application stack from the manager, see Section 3.1).
- **Burst workers are baked, not configured after boot.** Installing software at boot would add minutes to the measured scale-up path. Instead, Packer will build an AMI by running the same `common` and `docker` roles, with the worker image pre-pulled. At boot, only the Tailscale login and `swarm join` remain. The roles are kept free of Proxmox-specific steps so Packer can reuse them unchanged. [ADR 0003]
- **The hand-off: no hand-written inventory.** Terraform records every VM as an `ansible_host` resource with two labels, `cluster` (`swarm`/`k8s`/`none`) and `node_role` (`control`/`worker`/`observability`). Ansible reads the hosts straight from Terraform state, and one `constructed` inventory file turns the labels into groups such as `swarm_control` and `swarm_workers`. IPs are defined once, and all grouping rules sit in one file. AWS burst workers will carry the same labels as EC2 tags, so they fall into the same groups with no special case. This is the standard tag-based pattern that cloud inventory plugins use, applied to both tiers. [ANS]
- **Where it runs:** `plan`, `validate` and lint run on every pull request. `apply` and playbooks run only after a merge to `main`, on the self-hosted Pi runner behind a manual approval. It is the only machine with credentials to change real infrastructure.

**Correction.** The first design wrote Ansible group names directly into Terraform. It was replaced before use: regrouping would have needed a `terraform apply`, and it gave burst workers no uniform way into the same groups.

*[ANS] Ansible docs, "ansible.builtin.constructed" and "How to build your inventory"; `cloud.terraform` collection, `terraform_provider` inventory plugin. Details: `docs/ansible.md`. [ADR 0003] `docs/adrs/0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md`.*

**Pinned versions.** Every layer under the worker is pinned, so a run in week 10 uses the same software as a run in week 4, and an AMI baked later matches the on-prem VMs:

- **OS image:** the Ubuntu 26.04 cloud image is downloaded by date (`20260921`), not as "current".
- **Packages:** automatic updates are switched off, and apt reads from a dated Ubuntu archive snapshot (`20260929T000000Z`), so every run installs the same package versions.
- **Docker:** `docker-ce` 29.8.1 and `containerd.io` 2.3.6 are pinned and put on hold, so a later `apt upgrade` can't change them between runs.
- **Containers:** the app image is referenced by a `sha-<commit>` tag (one per commit, never reused), Redis by an exact version (`8.8.3-alpine`). Updating either is a reviewed commit.

### 2.3.x Guest CPU model: instruction-set parity across tiers

In the hybrid design, one queue job can run on either an on-premises VM or an EC2 burst worker. Both run the same container image. For their job durations to be comparable, the two tiers must expose **the same instruction-set extensions** to the guest. The reason is that the worker's CPU-bound code (image resizing and encoding with the ImageSharp library, in C#) uses .NET hardware intrinsics, and the .NET JIT compiler decides **at runtime**, when it compiles each method, which instructions to emit: AVX2 if the CPU reports it, and since .NET 8 also AVX-512. If one tier exposes AVX2 and the other doesn't, or if one exposes AVX-512, the same job runs through different machine code on each tier. Any difference in duration would then come from the CPU model rather than from the orchestrator or the burst mechanism, and this project is measuring the latter.

**Starting point and correction.** The template first used `x86-64-v2-AES`, the type the bpg Proxmox provider recommends as a safe default. According to the Proxmox VE documentation, the next level up, `x86-64-v3`, adds `avx, avx2, bmi1, bmi2, f16c, fma, movbe, xsave` compared with `x86-64-v2-AES`. So the v2-AES guest could not see AVX or AVX2. The AMD EPYC processors behind EC2's AMD instance types do expose AVX2 to their guests, so the default would have made on-prem workers artificially slower, and only on-prem.

**Decision: `host` on-prem, paired with `c6a.large` on AWS.**

| | On-prem worker VM | EC2 burst worker |
|---|---|---|
| Physical CPU | AMD Ryzen 7 5700G (Zen 3) | AMD EPYC 7R13, 3rd gen "Milan" (Zen 3) |
| Clock | 3.8 GHz base, up to 4.6 GHz boost | 3.6 GHz all-core turbo |
| Guest CPU model | `host` (all host flags passed through) | native |
| AVX2 / AVX-512 | yes / no | yes / no |
| What one vCPU is | one SMT thread (of 16) | one SMT thread |
| vCPU / RAM | 2 / 4 GB | 2 / 4 GiB (`c6a.large`) |

- **Why `host` and not `x86-64-v3`:** `x86-64-v3` would expose the AVX2 baseline but still hide some Zen 3 extensions that the EC2 guest *does* see, which recreates the mismatch at a smaller scale. `host` gives the on-prem guest the same Zen 3 feature set that `c6a` exposes. `host` has one drawback: VMs can't be live-migrated to a host with a different CPU. That doesn't apply here, because the whole on-prem tier is one physical host.

**Why `c6a` (EPYC 7R13).** The deciding criterion was that the same job should **run through the same code on the same kind of vCPU**. Raw speed was secondary. `c6a` matches the on-prem host on all three counts that affect how the worker's code runs. `m6a` and `r6a` use the same Milan CPU but come with 8 GiB or more of RAM at the smallest size, so they fail point 3:

1. **Microarchitecture:** Zen 3 on both sides, so the JIT generates the same code paths.
2. **What a vCPU is:** an SMT thread on both sides. Two vCPUs are two hardware threads, not two full cores.
3. **Size:** `c6a.large` is 2 vCPU / 4 GiB, the same as an on-prem worker VM, so one burst node is exactly one more worker node.

**Rejected alternatives:**

| Family | CPU | Single-thread (PassMark) | Why rejected |
|---|---|---|---|
| **`c6a`** | **EPYC 7R13 (Zen 3)** | **2,606** | **chosen** |
| `c7a` | EPYC 9R14 (Zen 4, "Genoa") | 2,920 | AVX-512 (the .NET JIT would use it only in the cloud); no SMT, so each vCPU is a full physical core |
| `c6i`/`m6i` and other Intel families | Xeon | n/a | AVX-512, and a different vendor's microarchitecture |
| `t3`/`t3a` | burstable | n/a | CPU credits: a CPU-bound worker runs out of credits and is throttled partway through a test, so results depend on time |

`c7a` is **closer in raw speed** (11% slower per thread than the 5700G, against 21% for `c6a`). That's the trap in choosing it: part of that closeness comes from AVX-512 code paths the on-prem tier can't run and from counting whole cores as vCPUs. Matching speed that way would mean the workload behaves differently on each tier. With `c6a`, the only remaining difference is clock speed, which can be measured and explained.

**Expected speed gap (prediction).** PassMark single-thread ratings give the Ryzen 7 5700G **3,281** (12,550 samples) against **2,606** for the EPYC 7R13 (12 samples), a ratio of **1.26×**. That matches the clock ratio independently: 4.6 GHz / 3.6 GHz = **1.28×**. So an on-prem job is expected to finish about 20–25% faster than the same job on a burst worker. The gap should be smaller during load tests: PassMark measures one busy thread at full boost, while during a test all 16 host threads are busy and the 5700G can't hold 4.6 GHz.

**What parity doesn't cover.** Matching instruction sets makes the *code paths* the same, not the *speed*. The on-prem host is also shared: OPNsense and the other VMs run on it, and vCPUs are oversubscribed. So per-job duration is **measured, not assumed**. Worker metrics carry a `tier` label (`onprem` / `aws`), and the job-duration histograms are reported per tier (Section 5). There they confirm or refute the prediction above.

**Effect on autoscaling.** The Swarm controller sizes replicas as `ceil(depth / jobs_per_replica)`, which assumes every replica drains the queue at the same rate. A burst replica is expected to drain about 20% slower, so bursting will scale up slightly too little. This is reported as a finding. A per-tier `jobs_per_replica` is the obvious refinement.

*Sources: AWS EC2 C6a and C7a product pages (processor, clock, size); Proxmox VE Administration Guide, "CPU Type"; Microsoft .NET documentation, hardware intrinsics and .NET 8 AVX-512 support; workload design: `docs/csharp-workload-v1.2.md` §12; PassMark, [Ryzen 7 5700G vs EPYC 7R13 vs EPYC 9R14](https://www.cpubenchmark.net/compare/5635vs4323vs4634/AMD-EPYC-9R14-vs-AMD-Ryzen-7-5700G-vs-AMD-EPYC-7R13).*

**How parity is checked.** Before each test series, the relevant CPU flags are recorded on one worker from each tier and compared:
```sh
grep -ow -E 'avx2|avx512f|fma|bmi2' /proc/cpuinfo | sort -u
```
The expected output is the same on both tiers: `avx2 bmi2 fma` and no `avx512f`. This shows what the CPU **advertises**. To show what the runtime **actually uses**, the worker logs `Avx2.IsSupported`, `Avx512F.IsSupported` and its vector width (256 bits expected on both tiers) at startup, and exports them as labels on an `hba_worker_info` metric (planned), so one Prometheus query compares both tiers for every test run.

### 2.3.y Guest disk options

- **`discard = "on"`:** `local-lvm` is thin-provisioned, and discard hands the space freed inside the guest back to the pool. Without it, the pool fills up over repeated test runs. [PVE]
- **`iothread = true`:** disk I/O runs on its own thread, so it doesn't compete with the vCPU threads whose performance is being measured. [PVE]
- **`ssd` not set:** it isn't supported on VirtIO Block (`virtio0`) disks. [PVE][bpg]

*[PVE] Proxmox VE Admin Guide, "QEMU/KVM Virtual Machines", https://pve.proxmox.com/pve-docs/chapter-qm.html; [bpg] bpg/proxmox provider, `proxmox_virtual_environment_vm` docs.*

### 2.3.z Credentials: one identity per job

Each automated component gets its own identity, so that a leaked credential only affects that component's job.

| Identity | Used by | Policy |
|---|---|---|
| Proxmox API token | Terraform (`proxmox/`) | dedicated token, not a user password or root |
| `tf-state-proxmox` (IAM) | Terraform (`proxmox/`) state backend; the Ansible pipeline, which reads the same state to build its inventory | AWS managed `AmazonS3FullAccess` |
| Ansible SSH key | Ansible pipeline (Pi runner) | key-only login to the VMs, written to a temp file per job and deleted afterwards |
| `GITHUB_TOKEN` | app image build (GitHub-hosted runner) | per-job token, `packages: write` only in the image job, push only on `main` |
| `terraform-ci` (IAM, planned) | Terraform (`aws/`) | AWS managed `AmazonVPCFullAccess` + S3 access |
| `burst-controller` (IAM, planned) | burst script | to be decided |

**Trade-off:** `AmazonS3FullAccess` covers every bucket in the account, not only the Proxmox state file. A leaked state key could therefore also change or delete the AWS root's state and the data bucket. AWS managed policies were preferred over hand-written ones for simplicity. Narrowing this key to the state file is possible later, using the policy example in HashiCorp's S3-backend documentation.
