# Observability: Prometheus and Grafana

**Status:** 2026-10-16. Milestone 1 (central stack on `obs-01`) is live: deployed by the `observability` role (PR #21), all node_exporter targets up. Milestone 2 (collection inside the swarm, §5) is live since 2026-10-14: redis and worker targets up, `redis_key_size{key="hba:jobs"}` queryable on `obs-01`. Milestone 3 (the run dashboard, §6) is built and was used for the first lab scale-up test on 2026-10-16; it is provisioned from the repo once branch `feat/run-dashboard` is deployed. Milestone 4 is planned.

**Audience:** anyone changing scrape targets, dashboards or the monitoring stack, and anyone reading the dashboards during a test run.

**Related:** [docs/ansible.md](ansible.md) (inventory and groups the targets come from), [docs/network.md](network.md) (why containers can reach lab hosts), [docs/csharp-workload-v1.2.md](csharp-workload-v1.2.md) §8 (the `hba_` metrics the workload exports).

---

## 1. What runs where

| Component | Runs on | How it is deployed | Status |
|---|---|---|---|
| Prometheus server | `obs-01` (10.0.20.30) | Docker Compose, `observability` role | milestone 1 |
| Grafana | `obs-01` | Docker Compose, `observability` role | milestone 1 |
| node_exporter | every inventory host | apt package `prometheus-node-exporter`, `node_exporter` role | live |
| redis-exporter | swarm manager | `monitoring` stack, `stacks` role | milestone 2 |
| Prometheus in agent mode | swarm manager, on overlay `hba_hba` | `monitoring` stack, `stacks` role | milestone 2 |
| Swarm autoscaler (`/metrics` on :8000) | swarm manager, on overlay `hba_hba` | `autoscaler` stack, `stacks` role ([autoscaler.md](autoscaler.md)) | live |

```
 swarm (overlay "hba")                          obs-01 (Docker Compose)
┌───────────────────────────────┐             ┌──────────────────────────────────┐
│ prometheus --agent            │ remote_write│ Prometheus server                │
│   scrapes: worker tasks, api, ├────────────▶│   scrapes: node_exporter on all  │
│   redis-exporter              │             │   hosts (:9100), itself          │
│   external_labels: cluster=swarm            │ Grafana                          │
│ redis-exporter ─▶ redis       │             │   datasource + dashboards from   │
└───────────────────────────────┘             │   the repo                       │
                                              └──────────────────────────────────┘
```

`obs-01` is not part of either cluster. It stays up while the Swarm and Kubernetes VM sets are switched, so the results from both orchestrators end up in one Prometheus.

## 2. Why two Prometheus instances

**The server on `obs-01` cannot scrape the workload directly.** Worker replicas have no published ports. Publishing one would not help: the routing mesh would send each scrape to a random replica, so the metrics of different replicas would be mixed into one series. redis-exporter has to sit on the overlay network to reach `redis` anyway.

**Moving the server into the swarm does not work either.** It would go down with the cluster, and the Kubernetes runs would end up in a different store.

**So:** an agent inside the cluster scrapes over the overlay and pushes with `remote_write` to the server, which runs with `--web.enable-remote-write-receiver`. Burst workers on AWS join the same overlay over Tailscale, so the agent picks up their tasks too. Kubernetes gets the same setup later (agent with `kubernetes_sd`, `cluster=k8s`), and comparison dashboards switch between orchestrators with one label.

The options that were considered are in [ADR 0005](adrs/0005-in-cluster-prometheus-agent-with-remote-write.md). How the agent is set up is in §5.

## 3. Access

| | URL (over Tailscale) | Login |
|---|---|---|
| Prometheus | `http://10.0.20.30:9090` | none |
| Grafana | `http://10.0.20.30:3000` | `admin` / GitHub secret `GRAFANA_ADMIN_PASSWORD` |

Both ports are reachable only from the lab VLAN and over the Tailscale subnet route. Nothing is published through OPNsense.

**The admin password is only applied once.** Grafana reads `GF_SECURITY_ADMIN_PASSWORD__FILE` when it first creates its database. After that, changing the secret and re-running Ansible changes the file but not the login. To change it later:

```bash
read -rs NEWPW    # type the password; not echoed, not saved in shell history
sudo docker compose -f /opt/observability/compose.yml exec grafana \
  grafana cli admin reset-admin-password "$NEWPW"
unset NEWPW
```

The CLI takes the password as an argument ([Grafana CLI docs](https://grafana.com/docs/grafana/latest/cli/)). Reading it into a variable first keeps it out of shell history. Then update the GitHub secret to match.

## 4. Prometheus

**Config file.** `templates/prometheus.yml.j2` in the role, rendered to `/opt/observability/` and mounted read-only.

**Targets come from the inventory.** The `node` job is generated from `groups['all']`: one target per host at `ansible_host:9100`, labelled `instance=<inventory hostname>` (so dashboards show `swarm-01` and not `10.0.20.12:9100`). A VM added in Terraform is scraped after the next Ansible run, with no change here. Hosts that are powered off (the cluster set not under test) show as `down`. That is expected.

**Scrape interval.** 15 s globally (`observability_scrape_interval`), for the jobs this server scrapes itself. Queue depth and worker metrics arrive faster: the agent inside the swarm scrapes them every 5 s and pushes the samples here (§5).

**Retention.** `120d` or `30GB`, whichever is reached first (`observability_retention_time`, `observability_retention_size`). The time limit keeps every test run until after the report deadline (2026-11-22). The size limit protects the 50 GB disk.

**Config changes restart Prometheus.** Compose does not notice when the contents of a bind-mounted file change, so the template task notifies a handler that restarts the `prometheus` service. The restart takes a few seconds, and no scrapes happen during it. **Do not deploy config changes during a test run.**

**On/off flags take no value.** `--web.enable-remote-write-receiver=true` makes Prometheus exit at startup with `unexpected true`, and Compose restarts it in a loop. The pipeline still reports success, because Ansible only checks that Compose applied the change. This took Prometheus down after the milestone 2 deploy (2026-10-14). Write on/off flags without a value, and after a flag change check that `http://10.0.20.30:9090/-/ready` answers.

## 5. Collection inside the swarm

| Repo file | Contents |
|---|---|
| `stacks/swarm/monitoring.yml` | the `monitoring` stack: redis-exporter and the agent, both pinned to the manager and attached to the overlay network of the `hba` stack |
| `stacks/swarm/prometheus-agent.yml` | the agent config: scrape jobs, `remote_write` to `obs-01`, `external_labels: {cluster: swarm}` |

**Deployment.** The `stacks` role copies the whole `stacks/swarm/` folder to `/opt/stacks/` on `swarm-ctl` and deploys the stacks listed in `stacks_deploy`, in order (`hba`, `monitoring`, `autoscaler`). `hba` comes first because the other two join the network it creates. Swarm prefixes network names with the stack name, so the network called `hba` in `hba.yml` is `hba_hba`, and `monitoring.yml` refers to it as an external network by that name.

**Scrape jobs.** Every 5 s (global in the agent config):

| Job | Targets | Gives |
|---|---|---|
| `redis` | `redis-exporter:9121` | `redis_key_size{key="hba:jobs"}`: the queue length, the autoscaling signal |
| `worker` | every running `hba_worker` task, found by `dockerswarm_sd_configs` (`role: tasks`) on port 8080 | the `hba_` metrics, with a `node` label |
| `autoscaler` | `autoscaler_controller:8000` (full service name: it is in another stack) | the controller's `hba_autoscaler_*` metrics: depth it saw, current and desired replicas, scalings ([autoscaler.md](autoscaler.md) §5) |

The `worker` job keeps a discovered target only if its service is `hba_worker`, its desired state is `running`, and its address is on `hba_hba`. A task has one address per network, and only the overlay address is reachable from the agent. The node hostname is copied into the label `node`.

**What to know when changing it:**

- **A wrong key name fails silently.** redis-exporter reports a key it cannot find as `0`, with no error. The first draft watched `hba.jobs` instead of `hba:jobs`: the queue held 3 jobs and the metric said 0. The controller would see an empty queue and never scale up. If queue depth stays at 0 under load, check `REDIS_EXPORTER_CHECK_SINGLE_KEYS` first.
- **An empty queue reports 0, not "no data".** Redis deletes a list when its last item is taken, but the exporter keeps the series at `0`. Queries need no `or vector(0)`.
- **Discovery refreshes every 5 s** (`refresh_interval`). With the default of 60 s, a new replica, including a burst worker, would appear in the metrics up to a minute after it started.
- **The agent runs as root**, because the image's default user `nobody` cannot open the Docker socket. The `:ro` on the socket mount does not limit the API: it only stops the socket file itself from being replaced. Anything running in the agent container has full control of the manager's Docker. Acceptable in the lab; name it in the limitations.
- **The config file is versioned by its content.** Swarm configs cannot be changed after creation, so `monitoring.yml` names the config `prometheus_agent_${PROMETHEUS_AGENT_CONFIG_VERSION:-1}`. The `stacks` role sets the variable to the first 12 characters of the file's SHA-1. A changed file gives a new config name, and Swarm restarts the agent with it. Old configs stay behind (`docker config ls`); remove them now and then with `docker config rm`. A manual `docker stack deploy` without the variable uses `prometheus_agent_1`, and fails with "only updates to Labels are allowed" once the file has changed. Set the variable by hand in that case.
- **If `obs-01` is down,** the agent keeps the unsent samples in its write-ahead log (volume `prometheus-agent-data`) and sends them when the server is back.

**Data age.** The controller sees queue depth after the 5 s scrape, the remote-write send delay and its own 5 s polling loop: roughly 5–15 s. This matters for the fairness rule in §8.

**Checking the config before a PR:**

```bash
docker run --rm -v "$PWD/stacks/swarm:/cfg:ro" --entrypoint promtool \
  prom/prometheus:v3.15.0 check config --agent /cfg/prometheus-agent.yml
```

**Testing on a local swarm.** Point `remote_write` in a scratch copy of the config at an unused address (`http://127.0.0.1:1/api/v1/write`). Otherwise the test data lands in the real Prometheus as `cluster="swarm"` and mixes with real runs. A single local node is a manager, so the worker tasks stay Pending (`node.role == worker`); remove the constraint on the local service only (`docker service update --constraint-rm 'node.role == worker' hba_worker`). The agent's target list is at `http://localhost:9090/api/v1/targets` inside its container.

**Checks after an apply,** on `obs-01`:

1. `up{cluster="swarm", job=~"redis|worker"}`: the redis target and one worker target per replica, all `1`. The `job` filter matters: the node_exporter targets of the Swarm VMs also carry `cluster="swarm"` (from the inventory), so `up{cluster="swarm"}` alone shows two results even when the agent sends nothing.
2. `redis_key_size{key="hba:jobs"}` returns a value. This is the "done" condition of milestone 2.
3. `count by (node) (up{job="worker"})` matches `docker service ps hba_worker`.

## 6. Grafana as code

Nothing is configured by clicking. Everything Grafana loads comes from the role:

| Repo path (in the role) | Mounted at | Contents |
|---|---|---|
| `templates/grafana-datasources.yml.j2` | `/etc/grafana/provisioning/datasources/` | the Prometheus datasource, URL `http://prometheus:9090` |
| `files/grafana/provisioning/dashboards/default.yml` | `/etc/grafana/provisioning/dashboards/` | provider pointing at `/etc/grafana/dashboards` |
| `files/grafana/dashboards/*.json` | `/etc/grafana/dashboards/` | own dashboards, committed: `hba-run.json`, the run dashboard (below) |
| `observability_grafana_community_dashboards` (defaults) | `/etc/grafana/dashboards/` | community dashboards, downloaded at deploy time (below) |

Grafana decides what a provisioning file is by its **folder** (`datasources/`, `dashboards/`), not its name. A misspelt folder (`dashboard/`) is ignored without an error.

**The datasource has a fixed `uid` (`prometheus`).** Dashboards written for this project refer to it by uid. A fixed uid keeps their JSON working on a rebuilt Grafana.

**The datasource is a template** because `jsonData.timeInterval` must equal the global scrape interval (`observability_scrape_interval`). Grafana derives `$__rate_interval` from it (at least 4× the value). Set it to the global interval, not the fastest per-job one: with 5 s there, the 15 s node jobs would get `rate()` windows with a single sample, and gaps in their panels. Grafana reads datasource files only at startup, so a change restarts Grafana. Dashboards are re-read every 30 s.

**Community dashboards are downloaded, not committed.** Each entry in `observability_grafana_community_dashboards` (grafana.com id, pinned revision, sha256) is fetched by `get_url` on `obs-01`. The rule: own dashboards are committed, unmodified third-party dashboards are downloaded.
- **Reproducible:** the sha256 must match, so different bytes upstream fail the run instead of loading unseen content.
- **Offline after the first run:** with a matching file on disk, `get_url` does not contact grafana.com at all.
- **Skipped in `--check`:** in check mode `get_url` sends a HEAD request, and grafana.com answers it with 405. PR dry runs therefore do not show a pending download.
- **No restart:** the provider rescans the folder every 30 s.

The first attempt committed the 15k-line JSON instead, and the first deploy shipped without it (the file was deleted locally before it was ever committed). An empty dashboards folder is valid, so nothing failed. Only the manual Grafana check caught it.

**Check how a community dashboard references the datasource.** Node Exporter Full (rev 45) uses a datasource *variable* (`${ds_prometheus}`, type `prometheus`). That resolves to the default Prometheus datasource at runtime and works unchanged. Older dashboards instead carry `${DS_PROMETHEUS}` import placeholders, which only the UI import dialog fills in. Provisioned as they are, their panels show "datasource not found". Such a dashboard cannot be used unchanged: replace the placeholders with uid `prometheus` and commit it as an own dashboard instead.

**Changing a dashboard:** edit it in the UI, then copy the JSON (*Save* on a provisioned dashboard offers this) into `dashboards/` and open a PR. Edits saved only in the UI are lost when the volume is rebuilt.

**Two folders, two jobs.** `provisioning/dashboards/` holds only the provider config (*where* to look). Grafana reads every file there as provider config, so a dashboard exported into it breaks provisioning. Dashboards go into `dashboards/` (plural), the folder mounted at `/etc/grafana/dashboards`.

**Exporting from the UI:** Export → JSON, the classic JSON model (Grafana 13 also offers the v2 resource format as YAML/JSON; not used here), with "Export for sharing externally" **off**, or the datasource becomes a `${DS_…}` placeholder. Then set a fixed `uid`, `"id": null`, and a file name without spaces.

**Previewing before a PR:** Dashboards → New → Import → upload the file, and in the import dialog change the uid (e.g. `hba-run-preview`) and the name. With the real uid, the preview would take the uid the provisioned copy needs. Delete the preview after the deploy.

### The run dashboard (`hba-run`)

One test run on one timeline, with shared crosshair: hovering over one panel marks the same moment in all of them. The `cluster` variable (`label_values(redis_key_size, cluster)`) switches between orchestrators; every query filters on it, so the same dashboard serves the Kubernetes runs.

| Panel | Query (all with `cluster="$cluster"`) | Shows |
|---|---|---|
| Queue depth | `redis_key_size{key="hba:jobs"}` | the autoscaling signal |
| Replicas | `hba_autoscaler_desired_replicas`, `hba_autoscaler_current_replicas`, `count by (tier) (hba_worker_info)`, `sum by (tier) (hba_jobs_in_progress)` | desired / scheduled / ready / busy |
| Replicas per node | `count by (node) (hba_worker_info)`, stacked | placement: 2 per worker, none on the control node |
| Throughput | `sum by (tier) (rate(hba_jobs_completed_total{status="done"}[…]))`, plus `failed` | jobs/s; flattens at the plateau |
| Job duration | `histogram_quantile(0.5 / 0.95, …hba_job_duration_seconds_bucket…)` by tier | processing time; the tool for preset calibration |
| Queue wait | the same on `hba_job_queue_wait_seconds_bucket` | how long jobs waited; grows at the plateau |
| Node CPU busy | `1 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle"}[…]))` | per cluster VM |
| CPU steal | `avg by (instance) (rate(node_cpu_seconds_total{mode="steal"}[…]))` | time the hypervisor gave to other VMs: vCPU oversubscription |

What to know when reading or changing it:

- **The four replica lines mean different things.** *Desired* is what the controller wants, *scheduled* is what Swarm reports, *ready* counts workers that are warmed up and scraped, *busy* counts workers processing a job. The gaps between them are the scaling delays. In the first lab test, ready reached 6 about 10 s after desired ([autoscaler.md](autoscaler.md) §8).
- **Desired and scheduled come from the Swarm controller.** Kubernetes has no `hba_autoscaler_*` metrics; those two lines will need KEDA/HPA queries for `cluster="k8s"`. Ready, busy and every other panel work unchanged.
- **Replica counts drawn as steps** (`stepAfter`): a smooth line would draw 3.5 replicas that never existed.
- **Min interval 5 s on the agent-fed panels** (all except the two CPU panels). That makes `$__rate_interval` 20 s instead of the 60 s derived from the datasource's 15 s, matching the agent's 5 s scrape. The CPU panels keep the default, because node_exporter is scraped every 15 s.
- **Queue wait buckets get coarse above 60 s** (60, 120, 300, 600), so high percentiles are interpolated within wide buckets.
- **Annotations:** the panel shows Grafana annotations tagged `hba-run`. The run script (planned, `loadtest/`) writes them through the HTTP API at the start and end of each run; until then they can be added by hand (Ctrl+click on a graph).
- **A fixed time window for screenshots:** `/d/hba-run?from=<epoch ms>&to=<epoch ms>&var-cluster=swarm&kiosk`. Chrome/Edge DevTools (F12, Ctrl+Shift+P, "Capture full size screenshot") capture the whole page. The image renderer plugin is not installed: it would live in the `grafana-data` volume outside the repo, and it needs a Chromium the Grafana image does not have. If rendered PNGs are ever needed, add the `grafana/grafana-image-renderer` container to the role's compose file.

## 7. Secrets and the public Actions logs

- `GRAFANA_ADMIN_PASSWORD` is a repo secret, passed as `env` to **both** jobs in `.github/workflows/ansible.yml`. The check job renders the templates too.
- The role reads it with `lookup('env', …)` and asserts that it is not empty, so a missing secret fails the run instead of setting an empty password.
- The task that writes the password file has `no_log: true` **and** `diff: false`. The pipeline runs with `--diff`, which would otherwise print the file contents in a world-readable log.
- The password reaches Grafana as a Compose secret (a file), not as a plain environment variable, so it does not show up in `docker inspect`.

## 8. Planned

**Milestone 2 leftovers.**
- Run the `promtool` check from §5 in the lint workflow.
- Scrape the API once it has a `/metrics` endpoint ([csharp-workload-v1.2.md](csharp-workload-v1.2.md) §8.2, not built yet). It is only needed for request-rate panels, not for autoscaling.

**Milestone 3 leftovers.** The dashboard is done (§6). The annotations it shows are written by the run script in `loadtest/` (planned), through the Grafana HTTP API with a service account token kept outside the repo. The same script exports each run's window with `query_range`, which also closes the backup gap in §10.

**Milestone 4 (weeks 8–10).**
- k6 → Prometheus remote write over Tailscale, so the request rate is on the same timeline as queue depth.
- node_exporter baked into the burst AMI.
- The Kubernetes agent.
- cAdvisor only if per-container CPU turns out to be needed.

**Fairness between the autoscalers.** The Swarm controller sees queue depth after scrape interval + remote-write delay + its own loop. KEDA reads Redis directly, every 30 s by default. Both should react to data of about the same age, or the comparison measures the configured intervals rather than the orchestrators. The chosen values go into an ADR before the comparison runs.

## 9. Common changes

**Add a scrape target outside the inventory:** add a job to `prometheus.yml.j2` and open a PR. Hosts in the inventory need no change (§4).

**Add a scrape target inside the swarm:** add a job to `stacks/swarm/prometheus-agent.yml`, run the `promtool` check (§5), and open a PR. The new config name is handled by the `stacks` role.

**Add an own dashboard:** put the JSON in `files/grafana/dashboards/`, check its datasource references (§6), and open a PR.

**Add or bump a community dashboard:** add or edit an entry in `observability_grafana_community_dashboards` (`name`, `id`, `revision`, `sha256`). Get the hash with `curl -sL https://grafana.com/api/dashboards/<id>/revisions/<rev>/download | sha256sum`, and check its datasource references (§6) first.

**Bump an image:** change the tag in `defaults/main.yml`. Check the tag exists on Docker Hub first. Prometheus tags have a `v` prefix (`v3.15.0`), Grafana tags do not (`13.2.3`). The agent in `stacks/swarm/monitoring.yml` uses the same Prometheus tag as the server; bump both together. Avoid bumping between comparison runs.

**Bump the Compose plugin** (docker role): read the exact string from `apt-cache madison docker-compose-plugin`. Compose jumped from v2 to v5 numbering, and Ubuntu 26.04 only has 5.x builds. The package is on hold, so a version change also needs `allow_change_held_packages: true` on the apt task.

## 10. Known gaps

- **`docker compose down -v` deletes all metrics.** The data is in named volumes. There is no backup of test-run data yet. Options: export each run's range queries to CSV/JSON after the run, or enable the admin API and take TSDB snapshots. Decide before the first comparison run.
- **No TLS or auth on Prometheus.** Acceptable while it is only reachable from the lab VLAN and the tailnet. The remote-write receiver accepts writes from anything that can reach port 9090, so a test agent on a laptop can push data labelled `cluster="swarm"` into the real store. Point test agents elsewhere (§5).
- **The agent controls the manager's Docker** through the socket (§5).
- **No alerting.** Nothing needs to page anyone. Alertmanager is out of scope.

## 11. Sources

- Prometheus: [installation (Docker)](https://prometheus.io/docs/prometheus/latest/installation/), [configuration](https://prometheus.io/docs/prometheus/latest/configuration/configuration/), [`dockerswarm_sd_config`](https://prometheus.io/docs/prometheus/latest/configuration/configuration/#dockerswarm_sd_config), [storage and retention](https://prometheus.io/docs/prometheus/latest/storage/), [command-line flags](https://prometheus.io/docs/prometheus/latest/command-line/prometheus/), [agent mode](https://prometheus.io/blog/2021/11/16/agent/), [Docker Swarm guide](https://prometheus.io/docs/guides/dockerswarm/)
- Grafana: [Docker](https://grafana.com/docs/grafana/latest/setup-grafana/installation/docker/), [environment variable overrides and `__FILE`](https://grafana.com/docs/grafana/latest/setup-grafana/configure-grafana/#override-configuration-with-environment-variables), [provisioning](https://grafana.com/docs/grafana/latest/administration/provisioning/), [Node Exporter Full (1860)](https://grafana.com/grafana/dashboards/1860-node-exporter-full/)
- Docker: [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/), [Compose plugin on Linux](https://docs.docker.com/compose/install/linux/), [Swarm configs](https://docs.docker.com/engine/swarm/configs/), [external networks](https://docs.docker.com/reference/compose-file/networks/#external)
- Ansible: [`community.docker.docker_compose_v2`](https://docs.ansible.com/projects/ansible/latest/collections/community/docker/docker_compose_v2_module.html), [handlers](https://docs.ansible.com/projects/ansible/latest/playbook_guide/playbooks_handlers.html)
- [redis_exporter](https://github.com/oliver006/redis_exporter)
