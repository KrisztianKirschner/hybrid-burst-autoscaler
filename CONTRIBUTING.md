# Contributing

Conventions for working in this repo. Keep it simple: small branches, small PRs, CI before merge.

## Workflow

1. Branch off `main`.
2. Push the branch and open a PR. Don't push directly to `main`.
3. Wait for CI (if it applies to your change), then merge with a **merge commit** (not squash), so each commit and its message stays in the history.
4. The branch is deleted automatically after merge. Clean up locally with `git fetch --prune` and `git branch -d <name>`.

## Branch names

`<type>/<short-description>`

| Type     | Use for                          | Example                            |
|----------|----------------------------------|------------------------------------|
| `feat/`  | new capability                   | `feat/swarm-autoscaler-controller` |
| `fix/`   | bug fixes                        | `fix/proxmox-vm-cpu-type`          |
| `chore/` | tooling, dependencies, config    | `chore/add-dependabot`             |
| `ci/`    | GitHub Actions workflows         | `ci/add-tflint`                    |
| `docs/`  | report, README, diagrams         | `docs/architecture-diagram`        |
| `test/`  | k6 scripts, load tests           | `test/k6-baseline-stages`          |

## Commit messages and PR titles

Lowercase, imperative, no type prefix:

```
feat: add Proxmox CI/CD workflow
fix: set VM cpu type in proxmox module
```

PRs are merged with a merge commit, so every commit message ends up on `main`: write each one as if it stood alone. The PR title becomes the merge commit's title.

## Naming

- **Files and directories:** lowercase, kebab-case (`policy.yaml`, `stacks/`). Two exceptions follow their tool's convention:
  - **Ansible roles and variables:** snake_case (`node_exporter`, `swarm_advertise_addr`). ansible-lint's `role-name` rule doesn't allow hyphens.
  - **.NET projects and source files under `app/`:** PascalCase (`Hba.Worker/`, `JobLoop.cs`).
- **Workflow files:** `<area>.yml`, e.g. `proxmox.yml`, `aws.yml`, `ansible-lint.yml`.
- **Terraform:** snake_case names, named by role and not by type: `aws_security_group.burst_workers`, not `aws_security_group.sg1`.
- **Cloud resources:** prefix with `hba-` (`hba-burst-worker`, `hba-k6-loadgen`) and tag everything `Project=hybrid-burst-autoscaler`. This keeps the AWS console filterable and IAM policies narrow.
- **Docker images:** `ghcr.io/<owner>/hba-api`, `hba-worker`. Tag with a git SHA or semver, not just `latest`.
- **Prometheus metrics:** `hba_` prefix with standard suffixes: `hba_jobs_completed_total`, `hba_job_duration_seconds`.

## Repo layout

```
terraform/proxmox/   on-prem VMs
terraform/aws/       AWS scaffolding
ansible/             node prep, swarm and k8s roles
app/                 api, worker, Dockerfiles
stacks/              swarm compose, k8s manifests
autoscaler/          swarm controller, KEDA ScaledObject
loadtest/            k6 scripts
docs/                report source
```

## Secrets

Never commit credentials. Use GitHub Secrets (`TF_VAR_*` for Proxmox, `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` for AWS).
