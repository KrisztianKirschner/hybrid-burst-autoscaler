# Security Assessments

**Status:** assessments, 2026-10-28. Nothing here has been changed in the repository, its settings or the tailnet yet; §3 lists what is proposed and what is still to check.

**Audience:** the repository owner and collaborators, and anyone reviewing how this project handles credentials and remote access.

**Summary:**
- **GhostAction** (GitHub Actions supply-chain campaign, §1): no indicator of compromise. The repository is a high-value target, though. Infrastructure secrets that are also stored at repository level, together with a self-hosted runner open to any workflow, mean that one stolen GitHub token would give an attacker the AWS account, the Proxmox host and root on the lab VMs.
- **Tailworm** (Tailscale SSH flaws, §2): not exposed, as far as the repository shows. No node is configured with Tailscale SSH. Three hand-configured nodes are still to be checked, and no fix has shipped yet.

Each section assesses one public threat against this project's setup. It records what was checked, how, and what is left. Items are marked:
- **verified:** checked directly;
- **reported:** stated by the owner, not checked;
- **inferred:** deduced from behaviour.

The course report treats this as a side topic: one line under *Limitations* points here.

---

## 1. GhostAction

### 1.1 Scope and method

**In scope:** the GitHub repository `KrisztianKirschner/hybrid-burst-autoscaler`, its workflows, its secrets and environments, and the self-hosted Raspberry Pi runner.

**Out of scope:** the cluster workloads.

All checks were **read-only**, done on 2026-10-28:

| Check | Source |
|---|---|
| Workflow files on every remote branch | `git` |
| Commits touching `.github/` (60 days) and all commit authors (90 days) | `git log` |
| Workflow runs since 2026-09-20 (about 300), with names, triggers and actors | GitHub REST API (public endpoints) |
| Credential patterns in the full git history: AWS keys, Tailscale keys, SSH private keys, GitHub tokens, Proxmox tokens | `git log -p` with pattern search |
| Ruleset on `main`, environments and their branch policies | GitHub REST API (public endpoints) |
| How workflows use secrets, runners and triggers | Workflow files on `main` |

**Limits:**
- Secret locations (repository level vs environment level) and account settings (2FA, personal access tokens, the security log) need admin access to the API, which wasn't used.
- The history scan covers the patterns listed above only. It is not a full secret scan.

### 1.2 The threat

GhostAction is a credential-theft campaign against GitHub Actions. It doesn't exploit a flaw in GitHub. It uses **stolen GitHub credentials** (personal access tokens, or logins harvested by infostealer malware) to push a workflow disguised as maintenance, such as `security-audit.yml` or "GitHub Actions Security". On the next push, that workflow reads the repository's Actions secrets and sends them over HTTP to an attacker-controlled endpoint.

| Wave | Reported scale | Notes |
|---|---|---|
| Sep 2025 (disclosed by GitGuardian) | 327 accounts, 817 repositories, 3,325 secrets | PyPI, npm, Docker Hub and AWS credentials. Secret names were taken from each repository's existing workflows. |
| Oct 8–9, 2026 (StepSecurity, GitGuardian) | Hundreds to tens of thousands of repositories, depending on the source | The newer variant also searches the working tree and the **full git history** for cloud, AI and SaaS credentials. The 2026 figures disagree between outlets and are provisional. |

Two properties of the attack matter here:
1. **It doesn't need `main`.** A workflow on any branch with an `on: push` trigger runs as soon as that branch is pushed.
2. **Rotating the stolen secrets isn't enough.** The GitHub credential that let the attacker in must be revoked as well, or the attack repeats.

### 1.3 Indicators of compromise: none found

| Check | Result |
|---|---|
| Workflow files on the five remote branches | Only the nine known workflows |
| Commits touching `.github/` (60 days) | Only the owner and Dependabot |
| Commit authors (90 days) | The owner; HDani13 (C# code under `app/` only, 2026-10-04); Dependabot; the image-bump workflow |
| Workflow runs since 2026-09-20 | Only known workflows. One "Proxmox Infrastructure CI/CD" run (`proxmox.yml`, 2026-09-20) is the owner's first pipeline, later renamed. Runs by `github-actions[bot]` are the bump PRs, waiting for approval. |
| Credential patterns in the full git history | No matches |

A malicious workflow on a branch that was later deleted still leaves its run in the run history. No such run exists.

### 1.4 Controls in place

| Control | State | Effect against GhostAction |
|---|---|---|
| 2FA on the owner's account | Reported | **Strong.** It makes the initial account takeover harder. It doesn't protect a token that has already been issued. |
| Ruleset "protect main": PR required, no force push, no deletion | Verified | **None.** The attack pushes to a new branch. The ruleset does keep a malicious workflow off `main` without a visible PR. |
| Code Owner review (`CODEOWNERS`: `* @KrisztianKirschner`) | The file exists, but **not enforced**: the ruleset has `require_code_owner_review: false` and 0 required approvals | **None.** With a single owner who authors every PR it can't be enforced without a bypass. A bypass belongs to the same identity an attacker would hold. |
| Environments `aws` and `proxmox`, deployments from `main` only | Verified | **Strong** for secrets stored *only* in those environments. A job on another branch that names the environment is rejected. |
| Fine-grained IAM policies for the CI users | Reported | **Limits the impact** of stolen AWS keys. |
| Secrets masked in logs (GitHub masking, `no_log` in Ansible, `sensitive` in Terraform) | Verified in code | **None.** The attack sends secrets out over HTTP; nothing goes through the logs. |
| Secret scanning | Reported | **Partial.** It catches secrets committed to the repository, which the history-scanning variant targets. It doesn't detect exfiltration. |
| Every action pinned to a commit SHA, updated by Dependabot | Verified | **None** against GhostAction. It protects against a compromised third-party action, a different supply-chain attack. |
| No `pull_request_target`, `workflow_run` or `issue_comment` triggers; fork PRs and Dependabot excluded from credentialed jobs | Verified | **Strong** against outside contributors. It doesn't help against someone with write access, who is the GhostAction attacker. |
| Workflow `permissions: contents: read` by default | Verified | **Limits** what the job's `GITHUB_TOKEN` can do. It doesn't limit secrets. |

### 1.5 Residual risks

**Likelihood is low for every item:** each needs a compromised GitHub account first. The ranking is by impact.

**R1: infrastructure secrets readable from any branch (impact: high).** Inferred.
- `terraform-plan` (both roots) and `ansible-check` run on PR branches **without** naming an environment, and still received their credentials. For example, the Proxmox plan succeeded on `feat/proxmox-tf-gateway` and `ci/ansible-after-terraform` on 2026-10-22, and the AWS plan on `feat/aws-gateway` on 2026-10-21.
- Environment secrets are only given to jobs that name the environment, so these secrets also exist at **repository level**:

| Secret | What an attacker gains |
|---|---|
| `TERRAFORM_AWS_ACCESS_KEY_ID` / `_SECRET_ACCESS_KEY` | Whatever the `tf-aws` IAM policy allows: create and destroy VPC, EC2 and IAM resources (cost and integrity) |
| `TERRAFORM_STATE_AWS_*` | Read and overwrite the Terraform state of both roots |
| `PROXMOX_ENDPOINT`, `PROXMOX_API_TOKEN` | Create, change and delete VMs on `homelab-core-01` |
| `ANSIBLE_SSH_PRIVATE_KEY` | Root (through `become`) on every lab VM |
| `TS_LAB_ROUTER_AUTH_KEY` | Join the tailnet as `tag:lab-router`, which advertises routes into the lab VLAN |
| `GRAFANA_ADMIN_PASSWORD`, `RUSTFS_*`, `DOCKERHUB_*` | Lower impact: lab-internal services and an image-pull token |

**R2: the self-hosted runner accepts any workflow (impact: high).**
- **Any workflow on any branch can run on the Pi.** It only has to say `runs-on: self-hosted`. Self-hosted runners on a personal-account repository can't be restricted to selected workflows; runner groups with that option are an organization feature.
- **A malicious job can stay on the Pi.** The runner is persistent, and **no job uses `container:`** (CLAUDE.md plans job containers, but the workflows don't use them), so jobs run directly as `gha-runner`. A malicious job can leave a process or scheduled task behind.
- **From the Pi it reaches the lab.** The Pi accepts Tailscale subnet routes, so it reaches the lab VLAN directly.
- **Unknown: outbound filtering on the Pi.** CLAUDE.md plans outbound-only HTTPS to GitHub, but whether that filter is in place wasn't verified. Where it is, exfiltration to an arbitrary host fails. Exfiltration to an allowed host, such as an attacker's gist, would still work.

**R3: a single maintainer (impact: accepted).** A PR rule on a solo repository adds visibility, not a second pair of eyes. Whoever holds the owner's account can merge to `main`, approve environment deployments and change settings. This is inherent to a one-person project and is accepted. The account controls in §3 are the real defence.

**R4: a second account with write access (impact: as R1 + R2).** HDani13 has committed to the repository. If that account has write access, its security is as important as the owner's. Its 2FA status was not verified.

**R5: no security policy or response procedure (impact: slower recovery).** There is no `SECURITY.md`, no written list of where each credential lives, and no rotation order. In an incident this costs time, and GhostAction's own lesson is that the order matters (§3, G5).

### 1.6 Credentials for PR-time jobs

R1 is fixed by keeping the infrastructure secrets only in the `aws` and `proxmox` environments. The PR-time jobs then need credentials of their own. There are three options:
- **Read-only credentials in a separate environment (recommended for Terraform).** `terraform plan` needs only read access: an AWS read-only policy plus write access to the state lock file, and a Proxmox token with the `PVEAuditor` role. A stolen copy then allows reading, not changing.
- **A preview environment with a required reviewer.** Possible for `ansible --check`, which needs the SSH key and can't be made read-only. An automated campaign can't pass the approval. A hands-on attacker with a full-scope token could approve it through the API, so this raises the bar without closing the gap.
- **Drop the credentialed PR previews.** Keep `ansible-lint`, `terraform validate` and `tflint`, and rely on the plan that runs on `main` before the apply. This is the simplest option, and it gives up the review preview.

**Not recommended:**
- **Enforcing Code Owner review.** With one owner it blocks the owner's own merges, and the bypass that fixes that also serves an attacker.
- **More log masking.** Logs are not the path this attack uses.

---

## 2. Tailworm

### 2.1 The threat

"Tailworm" is a set of **11 flaws in Tailscale and headscale**, published on 2026-10-25 by a researcher (Cyberdione Labs) **without coordinated disclosure**. Tailscale 1.104.2 and headscale 0.29.5 were assessed.

- **The headline flaw is in Tailscale SSH.** A race in Unix-socket forwarding can run the forwarded connection as root. Anyone allowed to log in through Tailscale SSH, even to an ordinary account, can become root on that node.
- **It needs a foothold.** The attacker must already control an approved device on the tailnet, so it is lateral movement, not an outside-in break-in. The "worm" claim (spreading node to node) was shown in a lab only, and is disputed.
- **The other findings** affect Tailscale Serve, app connectors, headscale and relay components. They range from access-rule bypasses to denial of service.
- **Status on 2026-10-28:** no fixed version and no Tailscale bulletin for Tailworm yet. The interim advice is to treat every Tailscale SSH grant as root access and to narrow or remove it.

### 2.2 Exposure

| Area | This project | State |
|---|---|---|
| Tailscale SSH | **Not used.** Tailscale SSH is opt-in per node (`tailscale up --ssh`). Neither the `tailscale` Ansible role (`ts-gateway`) nor the AWS gateway's user data passes `--ssh`. | Verified in code |
| The policy's `ssh` rule | The default rule allows `autogroup:member` → `autogroup:self` only. It doesn't cover the tagged routers (`tag:lab-router`, `tag:aws-router`). It still grants SSH to any member device that enables it. | Reported (the policy lives in the Tailscale admin console, not the repository) |
| Hand-configured nodes: laptop, CI Pi, OPNsense plugin | Not set up by code, so `--ssh` can't be ruled out from the repository. | **Open:** G-T1 in §3 |
| Serve, app connectors | Not used | Verified in code |
| headscale | Not used; the tailnet uses Tailscale's coordination server | Verified |
| Relays | Tailscale's hosted relays, not self-run ones | Verified |
| Subnet routers | **Used** (`ts-gateway`, the AWS gateway). The published findings don't name subnet routing, but an effect can't be ruled out from news coverage alone. | Watch the bulletin |

**Upgrades are manual.** The install script runs only on the first run (the role skips it once Tailscale is installed), and neither the role nor the gateway's user data turns on Tailscale's auto-update. A fix therefore reaches `ts-gateway`, the AWS gateway and the Pi only when someone upgrades them.

**Public-repository note:** this section records what is checked and closed. Tailscale versions per host are added only after the fix is installed.

---

## 3. Actions and open checks

In priority order. The effort estimates are rough.

| # | Action | Addresses | Effort | Done |
|---|---|---|---|---|
| G1 | **Remove the repository-level copies of the infrastructure secrets** (table in R1). Keep them only in the `aws` and `proxmox` environments. Give PR-time jobs their own credentials (§1.6). | R1 | 1–2 h | |
| G2 | **Audit the accounts:** review and revoke personal access tokens (`/settings/tokens`; prefer fine-grained tokens with expiry), OAuth and GitHub apps (`/settings/applications`) and active sessions, and read the security log (`/settings/security-log`). Ask HDani13 to do the same, or downgrade their access to *read* if write isn't needed. | R3, R4 | 30 min | |
| G3 | **Harden the runner:** verify, or add, outbound filtering on the Pi limited to the hosts CI needs (GitHub, GHCR, AWS endpoints, HashiCorp releases, Tailscale). Run jobs in the planned `container:` image. Rebuild the Pi from the image if compromise is ever suspected. | R2 | 2–4 h | |
| T1 | **Confirm Tailscale SSH is off** on the laptop, the Pi and OPNsense: `tailscale debug prefs` shows `"RunSSH": false`. Record the result here. | Tailworm | 10 min | |
| T2 | **Comment out the `ssh` section** of the tailnet policy. Tailscale SSH is then denied even if a node enables it by mistake. | Tailworm | 5 min | |
| T3 | **Upgrade Tailscale on every node** when a fix ships (`apt install --only-upgrade tailscale`, or the plugin update on OPNsense). Watch [tailscale.com/security-bulletins](https://tailscale.com/security-bulletins). | Tailworm | 20 min | |
| G4 | **Detection:** check the Actions run list for unknown workflow names during regular work; turn on Tailscale's new-device notifications; keep the AWS billing alert. | All | 15 min | |
| G5 | **Write a short `SECURITY.md` and rotation runbook.** On suspicion: revoke GitHub sessions and tokens **first**, then rotate the AWS keys, the Proxmox token, the SSH key and the Tailscale key, then Docker Hub, Grafana and RustFS; remove unknown Tailscale devices; check AWS for unknown resources; rebuild the Pi. | R5 | 1 h | |

---

## Sources

**GhostAction**
- GitGuardian: [GhostAction campaign: 3,325 secrets stolen](https://blog.gitguardian.com/ghostaction-campaign-3-325-secrets-stolen/) (Sep 2025)
- StepSecurity: [GhostAction campaign: over 3,000 secrets stolen](https://www.stepsecurity.io/blog/ghostaction-campaign-over-3-000-secrets-stolen-through-malicious-github-workflows) and [GhostAction returns](https://www.stepsecurity.io/blog/ghostaction-returns)
- The Hacker News: [GhostAction compromises about 1,900 accounts](https://thehackernews.com/2026/10/ghostaction-compromises-about-1900.html) (Oct 2026)
- Let's Data Science: [GhostAction injects credential-stealing GitHub workflows](https://letsdatascience.com/news/ghostaction-injects-credential-stealing-github-workflows-95439117)
- Wiz Cloud Threat Landscape: [GhostAction campaign](https://threats.wiz.io/all-incidents/ghostaction-campaign)
- SC World: [More than 800 GitHub repos affected](https://www.scworld.com/news/more-than-800-github-repos-affected-in-secret-stealing-campaign); BleepingComputer: [Hackers steal 3,325 secrets](https://bleepingcomputer.com/news/security/hackers-steal-3-325-secrets-in-ghostaction-github-supply-chain-attack)
- GitHub Docs: [Using secrets in GitHub Actions](https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions), [Managing environments for deployment](https://docs.github.com/en/actions/managing-workflow-runs-and-deployments/managing-deployments/managing-environments-for-deployment), [Security hardening: self-hosted runners](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#hardening-for-self-hosted-runners)

**Tailworm**
- SecurityWeek: [Tailscale rushes patches after uncoordinated disclosure of 'Tailworm' flaws](https://www.securityweek.com/tailscale-rushes-patches-after-uncoordinated-disclosure-of-tailworm-flaws/)
- BleepingComputer: [Tailscale warns of SSH flaws allowing root privilege escalation](https://www.bleepingcomputer.com/news/security/tailscale-warns-of-serious-ssh-flaws-allowing-root-privilege-escalation/)
- Konfirmity: [Tailscale SSH Tailworm vulnerabilities](https://www.konfirmity.com/advisories/tailscale-ssh-tailworm-vulnerabilities)
- The Cyber Security Guru: [Tailworm: Tailscale SSH Unix socket flaws explained](https://thecybersecguru.com/news/tailworm-tailscale-ssh-unix-socket-forwarding/)
- Tailscale: [Security bulletins](https://tailscale.com/security-bulletins)
- Tailscale Docs: [Tailscale SSH](https://tailscale.com/kb/1193/tailscale-ssh)
