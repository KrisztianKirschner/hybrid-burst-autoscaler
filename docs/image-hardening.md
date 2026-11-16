# Cloud Image Build and Hardening

**Status:** proposal, 2026-10-02. Nothing here is scheduled yet. Once agreed, the decisions in §7 become ADRs in `docs/adrs/`.

**Audience:** whoever builds the VM images and burst-worker AMIs (Packer, Ansible), and anyone reviewing why the images look the way they do.

**Summary:** Build the on-prem Proxmox template and the AWS burst-worker AMI from **one Packer template that runs the same Ansible roles**. Add CIS Level 1 hardening as a later, separate step. Use ansible-lockdown to apply it and OpenSCAP to check it independently. Do Packer first, because it shortens burst time-to-capacity. CIS hardening is a stretch goal and the starting point for the planned BSc thesis on compliance drift.

---

## 1. Problem

On-prem cluster VMs and AWS burst workers have to behave the same. A burst worker is a full-weight node that joins the same cluster, and the comparison assumes a job costs the same on either tier. Today:

- On-prem VMs are cloned from a plain Ubuntu cloud-image template ([template.tf](../terraform/proxmox/template.tf)), and Ansible configures them **after** boot.
- AWS burst workers don't exist yet. If they install Docker and join the cluster at boot, that install time is added to every burst event.

Two questions follow. How do we keep both tiers on the same base? Should that base be hardened?

## 2. Background: CIS Benchmarks

The [Center for Internet Security](https://www.cisecurity.org/cis-benchmarks) publishes free, consensus-based hardening guides. There is one per product: Ubuntu 24.04, Docker, Kubernetes, AWS Foundations, and so on. Each recommendation belongs to a profile:

| Profile | Meaning | Typical controls |
|---|---|---|
| **Level 1** | Practical baseline. It shouldn't break normal functionality and has little performance cost. | No root SSH login, unused filesystems and services disabled, file permissions, login policy, `auditd` |
| **Level 2** | Defence in depth for high-security environments. It can break things or cost performance. | Separate partitions (`/var`, `/tmp`, `/var/log`), AppArmor enforce everywhere, heavy audit rules |

Benchmarks are also split into **Server** and **Workstation** profiles. This project would use **Level 1 Server**.

The OS benchmark is only one layer. The **CIS Docker** and **CIS Kubernetes** benchmarks (audited with `docker-bench-security` and `kube-bench`) cover the cluster layer, which matters at least as much here.

## 3. Where the base image comes from

**Proposal:** build our own images with Packer, using the same recipe for both targets.

**Rejected: CIS Hardened Images from AWS Marketplace.** CIS sells pre-hardened AMIs, but:
- They only run on AWS. Marketplace AMIs can't be exported to Proxmox, so the two tiers would start from different bases.
- CIS charges an hourly fee on top of the instance price for every burst worker.
- The hardening is a black box. We can't see or version-control the exceptions we need (§5).

**Rejected (for burst workers): install everything at boot with cloud-init.** It is simple, but every burst event then waits for package installs and Docker setup before the node can join. That delay shows up directly in the measured time-to-capacity.

## 4. Proposed design: same recipe, two outputs

A literal single image isn't possible. Proxmox needs a template, AWS needs an AMI, and AWS Ubuntu runs a different kernel (`linux-aws`). The standard pattern is one template with two builders:

```
             ┌─ proxmox-clone builder ──→ Proxmox template ─┐
one Packer   │                                              ├─ same Ansible roles
template  ───┤                                              │  (common, docker, later CIS L1)
             └─ amazon-ebs builder ─────→ AMI ──────────────┘
```

**How Packer works:** it boots a temporary VM from the base image, connects over SSH, runs the provisioners (our existing Ansible roles), shuts down, saves the result as a template or AMI, and deletes the temporary VM. Unlike Terraform it keeps no state. The output is an image, not running infrastructure. Workflow: `packer init` → `fmt` → `validate` → `build`.

**Where it runs:** Packer only orchestrates. The build itself runs on the target (a Proxmox VM or an EC2 instance), so the arm64 Pi runner can build amd64 images without problems. The Proxmox build needs LAN access, and the Ansible provisioner needs a Linux controller. So builds run on the **Pi runner or WSL**, never on native Windows.

**Measurable payoff:** compare burst time-to-capacity with a pre-built AMI against an install-at-boot AMI. That result belongs to the thesis core.

## 5. Hardening (stretch goal)

Apply CIS Level 1 **during the Packer build**, using the same role on both outputs. Then audit both images with a different tool.

| | ansible-lockdown | Ubuntu Pro USG | OpenSCAP |
|---|---|---|---|
| What it is | Community Ansible roles (`UBUNTU24-CIS`, …), not an official CIS product | Canonical's official CIS / DISA-STIG tool | NIST SCAP scanner (`oscap`) + ComplianceAsCode content |
| Main job | Fixing (has a separate audit companion) | Fixing + auditing (`usg fix` / `usg audit`) | Auditing (can also generate fix scripts) |
| Exceptions | One variable per rule, kept in Git | Tailoring file | Tailoring file |
| Fit here | Good. We already use Ansible, and it behaves the same on both targets | Poor. Needs an Ubuntu Pro token attached during builds, which is awkward with images cloned many times and a public repo's CI | Good. Machine-readable reports (XML/HTML) |
| Catch | Lags new Ubuntu releases | Least hands-on learning | Ubuntu content packages lag; may need the upstream release |

**Proposal:** **ansible-lockdown applies, OpenSCAP audits.** The tool that applies the hardening shouldn't be the only one checking it.

### Known conflicts with this project

- **IP forwarding.** CIS L1 says "ensure IP forwarding is disabled". Docker, Swarm overlay networking, Kubernetes and Tailscale all need it enabled.
- **Host firewall.** L1 expects a default-deny firewall. Docker and Kubernetes manage their own iptables rules, and Swarm needs ports 2377, 7946 and 4789 open between nodes.
- **Ubuntu 26.04 support.** Our template uses 26.04 (`resolute`). CIS benchmarks and ansible-lockdown roles usually arrive months after a new LTS. Check whether support exists before starting. If it doesn't, use 24.04 or apply the 24.04 benchmark as an approximation and say so.
- **Measurement fairness.** `auditd` and similar controls cost some CPU. That is acceptable only if every tier and both orchestrators get identical hardening, which the shared recipe guarantees.

Every disabled rule goes in a **documented exceptions list** with the reason, the way real organisations handle it. That list is a candidate ADR.

## 6. Sequencing

| Phase | What | When |
|---|---|---|
| 1 | Packer, `proxmox-clone` builder only, reusing `roles/common` and `roles/docker` | After M2 (Swarm baseline) |
| 2 | Add the `amazon-ebs` builder | Once the AWS scaffolding (VPC, subnet, SG) exists, before the first burst run (M4) |
| 3 | CIS L1 via ansible-lockdown + OpenSCAP audit, with the exceptions list | Stretch goal. Drop it if the timeline is tight |

Learn Packer cheaply first with HashiCorp's [Docker get-started tutorial](https://developer.hashicorp.com/packer/tutorials/docker-get-started). It runs on Docker in WSL and costs nothing.

**Link to future work:** an OpenSCAP report taken at build time is a measured "compliant at deploy" baseline. Scanning again later and comparing the reports measures post-deployment compliance drift, the planned BSc thesis topic. Kept out of this project's scope.

## 7. Open questions / ADR candidates

1. Bake images with Packer, or install at boot with cloud-init? (Proposed: Packer.)
2. Do CIS L1 hardening in this project at all, or leave it for the thesis?
3. Which hardening tool? (Proposed: ansible-lockdown + OpenSCAP.)
4. Ubuntu 24.04 or 26.04, depending on benchmark and tool support.
5. The CIS exceptions list (forwarding, firewall, ports).

## References

- [Packer HCL templates](https://developer.hashicorp.com/packer/docs/templates/hcl_templates)
- [Packer Proxmox plugin](https://developer.hashicorp.com/packer/integrations/hashicorp/proxmox)
- [Packer Amazon plugin](https://developer.hashicorp.com/packer/integrations/hashicorp/amazon)
- [Packer Ansible provisioner](https://developer.hashicorp.com/packer/integrations/hashicorp/ansible)
- [CIS Benchmarks](https://www.cisecurity.org/cis-benchmarks)
- [ansible-lockdown/UBUNTU24-CIS](https://github.com/ansible-lockdown/UBUNTU24-CIS)
- [Ubuntu Security Guide (USG)](https://ubuntu.com/security/certifications/docs/usg)
- [OpenSCAP](https://www.open-scap.org/) and [ComplianceAsCode/content](https://github.com/ComplianceAsCode/content)
