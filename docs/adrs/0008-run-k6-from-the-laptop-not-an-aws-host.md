---
status: accepted
date: 2026-10-24
decision-makers: Krisztián Kirschner
---

# Run k6 from the laptop, not from an AWS host

## Context and Problem Statement

The project plan put the k6 load generator on its own EC2 instance. Two reasons were behind it: there was no other clean place to generate load from (`homelab-core-02` does not exist), and the generator must not share CPU with the system being measured. That plan carried a lot of work: a Packer AMI and its build pipeline, an IAM user for Packer, a Terraform instance, a route for the cluster VIP through the AWS gateway, and a Route 53 private zone so the instance could resolve `api.hba.internal`.

By 2026-10-24 two full runs from a laptop over Tailscale had been done (pilot 1 and the smoke runs, [loadtest.md](../loadtest.md) §4). Does the load generator still need to be in AWS?

## Decision Drivers

* **The generator must not distort the measurement.** It must not share CPU with a cluster VM, and it must be able to keep the planned arrival rate.
* **Time.** The report is due on 2026-11-22. The work still missing is the burst automation and the Kubernetes cluster with KEDA; those are what the thesis is about.
* **Same conditions in every run**, for both orchestrators and with or without bursting.
* **What the thesis reports is measured on the cluster side:** queue depth, replicas, job time and drain time. Where the arrivals come from does not change them.

## Considered Options

* An EC2 instance in the burst VPC, baked with Packer (the original plan)
* **The author's laptop, over Tailscale**
* A lab VM (for example `obs-01`)
* The CI Raspberry Pi

## Decision Outcome

Chosen option: "The author's laptop, over Tailscale". It is outside the cluster, so it does not share CPU with anything measured, which was the real concern behind the isolated host. The load is small: pilot 1 sent 1,712 jobs over 9 minutes at up to 4 requests per second, each accepted in a median of 7 ms (p95 9.2 ms), with 0 failed and 0 dropped iterations.

Every run must satisfy these validity checks, recorded with its results:

* `dropped_iterations` is 0 and no request failed. If k6 could not keep the rate, the generator and not the cluster set the load, and the run is repeated.
* The laptop is on mains power, with sleep off, on the same network as before, and nothing else heavy is running.
* The same script, `loadtest/k6/baseline.js`, and the same fixed load profile ([loadtest.md](../loadtest.md) §2).

### Consequences

* Good, because it removes about one to two days of work (Packer template, Packer IAM user, Terraform instance, VIP route, Route 53 zone), time that goes to the burst automation and Kubernetes instead.
* Good, because the pilot already shows the generator is far from its limit, so the result would not be better from AWS.
* Good, because it needs no AWS cost and no extra thing to forget and leave running.
* Bad, because the result depends on a laptop and a home line. The checks above make a bad run visible, but they cannot prove the laptop never affected a run that passed them.
* Bad, because the run is less automatic: it needs a person at the keyboard and a machine that stays awake. The planned run script (annotations, waiting for the queue to drain) is then the only automation.
* Bad, because the original design argument ("the load generator is isolated from what it measures, in a separate network") is now weaker. It is named as a limitation in the report.
* Neutral: the first Packer bake is now the burst AMI (after Kubernetes), not the k6 AMI.

## Pros and Cons of the Options

### An EC2 instance in the burst VPC

* Good, because it is the cleanest isolation and fully scripted.
* Good, because the generator is in the same cloud as the burst tier.
* Bad, because of the work listed above, for no change in what is measured.

### A lab VM (for example `obs-01`)

* Good, because it is on the lab network, with no tunnel in the path.
* Bad, because `obs-01` runs Prometheus and RustFS, and a generator there would share its CPU and disk with the monitoring that records the run.

### The CI Raspberry Pi

* Good, because it is always on and has no sleep problem.
* Bad, because the Pi is meant to stay single-purpose (CI only), and an arm64 k6 adds a second job to the only machine with infrastructure credentials.

## More Information

**Revisit if** a run shows `dropped_iterations` above 0 or the acceptance time rising towards the threshold, if the planned load grows by an order of magnitude, or if a reviewer needs the generator in AWS. At that point the original plan is still valid and the pieces it needs are listed in the Context.

**What this changes in the docs:** the k6 host is removed from [loadtest.md](../loadtest.md) §5, [aws.md](../aws.md), [cluster-vip.md](../cluster-vip.md), [ansible.md](../ansible.md), the workload doc and the report's architecture table. CLAUDE.md still describes the AWS k6 instance in its architecture and timeline sections; it lags behind this decision, as it does behind several others.

Related: [ADR 0007](0007-site-to-site-with-two-linux-tailscale-routers.md) (the link the k6 host was to use), [ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md) (Packer for burst workers), [docs/loadtest.md](../loadtest.md).
