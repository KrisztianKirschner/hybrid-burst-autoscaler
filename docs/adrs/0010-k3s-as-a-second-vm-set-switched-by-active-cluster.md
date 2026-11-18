---
status: accepted
date: 2026-11-16
decision-makers: Krisztián Kirschner
---

# Kubernetes as k3s on a second VM set, switched on and off by `active_cluster`

## Context and Problem Statement

The comparison needs a Kubernetes cluster of the same shape as the Swarm one (1 control + 3 workers, 2 vCPU / 4 GB each, [ADR 0006](0006-cluster-size-one-control-three-workers.md)) on the same host, `homelab-core-01` (32 GB RAM). The Swarm cluster was already measured (pilot 3, burst runs 2026-11-16) and had to stay repeatable. Both clusters at once need 38 GB, so only one can run at a time.

Four questions had to be answered together: which Kubernetes distribution, where its nodes live, which addresses they get, and how the host switches between the two clusters. Both people on the project were Kubernetes beginners, and the report was due 2026-11-22.

## Decision Drivers

* **Swarm stays measurable.** Nothing may change on the Swarm VMs; going back must be a power-on, not a rebuild.
* **The same shape and path for both,** so a difference in results comes from the orchestrator: same VM size, same template, same VIP and URL, same Tailscale path to AWS.
* **Time and experience.** Fewer moving parts to install and wire by hand.
* **Automation.** The switch must go through the pipeline, not through clicks in Proxmox.
* **The host's RAM.** Two clusters on at once must be impossible by accident.

## Considered Options

* Distribution:
  * **k3s** (one binary; bundles containerd, flannel, CoreDNS, kube-proxy, ServiceLB and a Helm controller)
  * kubeadm (upstream; every add-on installed and wired by hand)
* Where the nodes live:
  * **A second VM set, powered on only when tested**
  * The same four VMs, rebuilt for each orchestrator
* Addresses:
  * **The four free addresses in the advertised `10.0.20.8/29`: `.8`, `.9`, `.10`, `.15`**
  * A second block (`.21–.24`, the first plan in the report), advertised separately
  * Reusing `.11–.14`
* The switch:
  * **One Terraform variable, `active_cluster`, that sets each VM's `started`/`on_boot` and is passed to Ansible**
  * Powering VMs on and off by hand in Proxmox

## Decision Outcome

**Chosen:** k3s `v1.37.2+k3s1` on a second VM set (`k8s-ctl` 10.0.20.8, `k8s-01..03` 10.0.20.9/.10/.15, VMIDs 221–224), with `active_cluster` (`swarm`, `k8s` or `none`) in `terraform/proxmox/variables.tf` as the only switch. Terraform powers on the active cluster's VMs and every `cluster = "none"` VM; Ansible runs with `--limit active` and enables keepalived (the VIP) only on the active cluster. Details: [terraform.md](../terraform.md) §5.1, [kubernetes.md](../kubernetes.md), [network.md](../network.md) §2.3.

The power switch was built first (PR #90): the bpg provider starts VMs by default, so adding the K8s VMs without it would have powered on all eight.

### Consequences

* Good, because the Swarm VMs were never touched, and switching is a pull request: change one word, merge, and the pipeline does the rest. The switch to Kubernetes was verified on 2026-11-17.
* Good, because k3s brought the network, DNS, load balancer and Helm installs with it. The cluster was running with its first app in one day.
* Good, because the new addresses needed no new Tailscale route or grant, and each VM keeps its own address for good.
* Good, because a typo in `active_cluster` fails Terraform's validation instead of stopping every VM.
* Bad, because k3s is not upstream Kubernetes in every detail: SQLite instead of etcd, and kubelet, kube-proxy and flannel in one agent process. The report's Kubernetes numbers are k3s numbers. Its worker agents use 1.3–1.7 % of a VM while idle, against 0.8–1.0 % for Swarm's; whether that explains part of the 2.5 % slower jobs is open ([loadtest.md](../loadtest.md) §4.7, finding 2).
* Bad, because with `--limit active` the stopped set is never contacted, so its keepalived keeps its last state. A set powered on by hand would claim the VIP next to the active one. Rule: never power a set on by hand.
* Bad, because the switch back to Swarm has not been run yet.
* Neutral: the first apply of the new VMs failed to reach them from the CI Pi, because their return routes come from Ansible itself. Fixed by masquerading tailnet traffic on `ts-gateway` ([network.md](../network.md) §2.3), which keeps any new or rebuilt VM reachable from its first boot.

## Pros and Cons of the Options

### kubeadm

* Good, because it is upstream Kubernetes, the "real" thing the course names.
* Bad, because the network plugin, load balancer, storage and Helm all have to be chosen, installed and debugged separately, by two beginners, a week before the deadline.

### The same VMs, rebuilt per orchestrator

* Good, because it needs no extra disk or VM definitions.
* Bad, because every switch is a full rebuild, and the Swarm measurements could only be repeated on a rebuilt cluster, not the measured one.

### A second advertised block (`.21–.24`)

* Good, because the numbering is tidier.
* Bad, because it needs a second route on `ts-gateway`, new Tailscale grants and changed return routes, for no gain.

### Reusing `.11–.14`

* Good, because no address changes anywhere.
* Bad, because two VMs per address means stale ARP entries and SSH host keys on every switch, and both sets could never be defined as plain, independent Terraform entries.

### Powering VMs by hand

* Good, because it needs no code.
* Bad, because the next apply would start them again, and nothing would stop both sets running at once.

## More Information

Planned 2026-11-09 (distribution, addresses), coexistence confirmed 2026-11-16, built 2026-11-16/17 (PRs #90 power switch, #91 k3s role). Traffic stays off the control node through a taint, Traefik is off on both clusters (it may go on later for both, never only one), and the worker's CPU request is 1, as Swarm's reservation (decided 2026-11-17, [kubernetes.md](../kubernetes.md) §1–2).

Related: [ADR 0002](0002-terraform-labels-ansible-constructed-inventory.md) (labels; the `active` group extends it), [ADR 0006](0006-cluster-size-one-control-three-workers.md) (cluster size and the host budget), [ADR 0007](0007-site-to-site-with-two-linux-tailscale-routers.md) (the network path).
