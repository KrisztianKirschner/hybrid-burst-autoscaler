---
status: accepted
date: 2026-09-30
decision-makers: Krisztián Kirschner
---

# Configure on-prem VMs with Ansible after boot, bake AWS burst workers with Packer

## Context and Problem Statement

The project has two kinds of worker nodes that must end up configured the same way: long-lived on-prem VMs on Proxmox, and short-lived EC2 burst workers that join the same cluster during a load test. The on-prem VMs are configured by Ansible after Terraform creates them, run from the CI pipeline behind a manual approval. Can the burst workers be configured the same way, and if not, how do both tiers still end up identical?

## Decision Drivers

* **Time to capacity is measured.** The delay between "burst decided" and "burst worker taking jobs" is part of the result the report shows. Every second spent installing software at boot delays the lift in the graphs.
* **Burst workers start without a human.** They are launched by the burst script in the middle of a load test. Nothing on that path can wait for a pipeline or an approval.
* **Both tiers must run the same configuration**, or job durations differ for reasons unrelated to the orchestrators. The same rule already drove the CPU model choice and the apt snapshot.
* **One source of configuration.** The Ansible roles are the definition of a worker node. A second, hand-written copy for AWS would drift.
* **Hands-on course goal.** Using a further HashiCorp tool (Packer) is a plus, as long as the effort stays small.

## The two tiers

| | On-prem VMs | AWS burst workers |
|---|---|---|
| Lifetime | weeks | minutes to hours, one load test |
| Created by | Terraform, rarely | the burst script, during a load test |
| Is creation time critical? | no | **yes, it is part of what is measured** |
| Who starts the configuration | the CI pipeline, after manual approval | nobody: it must be ready at boot |

A Swarm VM is created once and then runs for weeks. Whether Ansible needs five minutes to configure it has no effect on any measurement. A burst worker exists only because load has already exceeded on-prem capacity. From that moment the queue keeps growing until the new node takes jobs.

## The burst critical path

```
queue depth exceeds on-prem capacity
  → burst script calls RunInstances
  → EC2 instance starts, OS boots
  → Tailscale joins the mesh
  → docker swarm join
  → worker image pulled from GHCR
  → worker warms up, starts taking jobs
```

| Step | Rough duration (to be measured) | Can it be shortened? |
|---|---|---|
| EC2 launch + boot | tens of seconds to about a minute | little: instance type, a lean image |
| Installing software at boot (apt upgrade, Docker, collections) | minutes | **yes: do it at bake time** |
| Tailscale + Swarm join | seconds | little |
| Pulling the worker image | seconds to tens of seconds | **yes: pre-pull it into the image** |
| Worker warm-up | set by the application | not in scope here |

Only the two "yes" rows can be removed, and both move work from boot time to image-build time.

## Considered Options

* Ansible after boot for both tiers (the burst script runs Ansible against each new instance)
* Cloud-init user-data script only, for burst workers
* **On-prem: Ansible after boot. AWS: Packer bakes an AMI by running the same Ansible roles**
* Packer for both tiers (Proxmox template and AMI from one build)

## Decision Outcome

Chosen option: "On-prem: Ansible after boot. AWS: Packer bakes an AMI by running the same Ansible roles".

**On-prem** stays as built: Terraform clones the VM template, and the Ansible pipeline configures the VMs after approval.

**AWS:** Packer's `amazon-ebs` builder starts from an Ubuntu 26.04 AMI and runs the project's Ansible roles (`common`, `docker`) through Packer's `ansible` provisioner. It also pre-pulls the pinned worker image. The result is a versioned AMI. The burst script launches instances from that AMI, and a short cloud-init script at boot only does the Tailscale login and `swarm join`.

Both tiers get the same configuration because they share its three sources:

* **the same Ansible roles**, run after boot on-prem and at bake time for AWS;
* **the same apt snapshot** (`common_apt_snapshot`), so an AMI baked in week 8 has exactly the packages Ansible installed on-prem in week 4;
* **the same pinned Docker version and worker image digest.**

Implementation belongs to the burst-tier work (weeks 8–9). Until then, the Ansible roles must stay free of anything Proxmox-specific so that Packer can reuse them unchanged.

### Consequences

* Good, because installing and upgrading software disappears from the burst critical path. What remains is boot, join and warm-up.
* Good, because the Ansible roles stay the only definition of a worker node. AWS does not get a second, hand-written copy.
* Good, because the CI pipeline and its approval gate stay out of the scale-up path entirely.
* Good, because it adds hands-on use of Packer, one more HashiCorp tool next to Terraform.
* Bad, because there is one more tool and one more build to maintain.
* Bad, because the AMI has to be rebuilt whenever the roles, the snapshot date, the Docker version or the worker image digest change. The rule: **bump these together, rebuild the AMI and re-run Ansible on-prem in the same change window.** An AMI built from older inputs is a drifted tier.
* Neutral, because an AMI costs a little EBS snapshot storage (cents per month).
* Neutral, because the base images still differ: the Proxmox template comes from Ubuntu's cloud image, the AMI from Canonical's EC2 image, and the kernel flavour will likely differ between them. The apt snapshot aligns the packages, not the kernel. The running kernel (`uname -r`) is recorded per tier alongside the CPU-flag check.

## Pros and Cons of the Options

### Ansible after boot for both tiers

* Good, because there is a single mechanism and no new tool.
* Bad, because apt upgrade, Docker install and the Galaxy collection download (60–120 s in CI on its own) all land on the burst critical path: several minutes before the first extra job runs.
* Bad, because the burst script then needs Ansible, an SSH key and state access at runtime, and a failed or slow Galaxy call can stop a scale-up.

### Cloud-init user-data script only

* Good, because it needs no extra tool, and EC2 runs it natively at boot.
* Bad, because the configuration is written a second time, in shell, next to the Ansible roles, and the two drift apart.
* Bad, because software is still installed at boot, so it stays on the critical path.

### On-prem: Ansible after boot. AWS: Packer AMI with the same roles

* Good, for the reasons under Consequences.

### Packer for both tiers

* Good, because both tiers would come out of one build: the strongest possible consistency.
* Bad, because the working Terraform + Ansible path for Proxmox would have to be rebuilt for little gain, since the apt snapshot already aligns the packages.
* Neutral: a reasonable "if we did it again" note for the report, not worth the time within this project.

## More Information

**Open for the implementation:**

* How the join secrets reach a new instance. The Tailscale auth key and the Swarm join token must not be baked into the AMI. Plain user-data is readable through the instance metadata service. Candidates: an ephemeral, pre-authorised Tailscale key, and fetching the Swarm token from SSM Parameter Store through the instance role at boot.
* Where the current AMI ID lives, so the burst script always launches the latest bake. Candidates: an SSM parameter, or a Terraform output of `terraform/aws`.
* Whether Packer runs in CI (a GitHub-hosted runner is enough, since it only talks to AWS) or by hand at each change window.

**Related:** [ADR 0002](0002-terraform-labels-ansible-constructed-inventory.md) (inventory). With this decision, burst workers are never in the CI inventory, because they are configured at bake time, not over SSH.

Sources:

* [Packer Ansible integration (`ansible` and `ansible-local` provisioners)](https://developer.hashicorp.com/packer/integrations/hashicorp/ansible)
* [Packer Amazon integration (`amazon-ebs` builder)](https://developer.hashicorp.com/packer/integrations/hashicorp/amazon)
