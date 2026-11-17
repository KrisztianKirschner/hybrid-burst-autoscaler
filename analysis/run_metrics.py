"""Run figures for the load-test comparison (docs/loadtest.md §4.7–§4.9), from Prometheus on obs-01.

Every figure is computed the same way for Swarm and Kubernetes runs, from the k6 start time and
the queue itself, so the two orchestrators are compared by one method. Reproduces the hand-made
Swarm figures of docs/loadtest.md §4, §4.5 and §4.6.

    python analysis/run_metrics.py                  # the run table (§4.7)
    python analysis/run_metrics.py isolation        # all conditions, compact (§4.8)
    python analysis/run_metrics.py burst            # burst runs, both orchestrators (§4.9)
    python analysis/run_metrics.py idle k8s 2026-11-17T14:44:30Z [minutes]   # idle CPU per VM

Needs the tailnet (obs-01 at 10.0.20.30) and nothing outside the standard library; the burst
mode also calls the AWS CLI (read-only) for the Auto Scaling group's activity history.
On Windows, set PYTHONIOENCODING=utf-8 for the arrows in the labels.
"""
import json
import subprocess
import sys
import urllib.parse
import urllib.request
from datetime import datetime

PROM = "http://10.0.20.30:9090/api/v1/"
STEP = 5  # seconds: the agents' scrape interval

# name, cluster label, k6 start (UTC, from the k6 output). Runs with max 6 replicas, no bursting
RUNS = [
    ("swarm pilot 3", "swarm", "2026-10-24T14:50:59Z"),
    ("swarm on-prem 2", "swarm", "2026-11-16T22:23:50Z"),
    ("swarm on-prem 3", "swarm", "2026-11-16T22:42:22Z"),
    ("k8s on-prem 1", "k8s", "2026-11-17T13:50:17Z"),
    ("k8s on-prem 2", "k8s", "2026-11-17T14:08:14Z"),
    ("k8s on-prem 3", "k8s", "2026-11-17T14:26:09Z"),
]

# §4.8: temporary live changes on the Kubernetes cluster, reverted afterwards
ISOLATION_RUNS = [
    # KEDA's Prometheus scaler on the Swarm controller's query: Swarm's signal age, otherwise §4.7
    ("k8s prom 1", "k8s", "2026-11-17T15:51:59Z"),
    ("k8s prom 2", "k8s", "2026-11-17T16:09:47Z"),
    ("k8s prom 3", "k8s", "2026-11-17T16:27:37Z"),
    # KEDA and the HPA at their defaults (15 s decisions, ±10 % band, default scale-up limits)
    ("k8s defaults", "k8s", "2026-11-17T15:17:20Z"),
]

# §4.5 and §4.9: max 10 replicas, up to two c7a.large burst nodes
BURST_RUNS = [
    ("swarm burst 1", "swarm", "2026-11-16T21:12:10Z"),
    ("swarm burst 2", "swarm", "2026-11-16T21:46:51Z"),
    ("swarm burst 3", "swarm", "2026-11-16T22:05:13Z"),
    ("k8s burst 1", "k8s", "2026-11-17T21:35:26Z"),
    ("k8s burst 2", "k8s", "2026-11-17T21:53:55Z"),
    ("k8s burst 3", "k8s", "2026-11-17T22:12:33Z"),
]
BURST_GROUP = {"swarm": "hba-burst-swarm", "k8s": "hba-burst-k8s"}
AWS_REGION = "eu-central-1"

# The autoscaler's decision and the replica count it set: Swarm controller, or the HPA KEDA drives
DESIRED = {
    "swarm": 'hba_autoscaler_desired_replicas{cluster="swarm"}',
    "k8s": 'kube_horizontalpodautoscaler_status_desired_replicas{cluster="k8s",horizontalpodautoscaler="keda-hpa-worker"}',
}
SCHEDULED = {
    "swarm": 'hba_autoscaler_current_replicas{cluster="swarm"}',
    "k8s": 'kube_deployment_spec_replicas{cluster="k8s",deployment="worker"}',
}


def get(path, **params):
    url = PROM + path + "?" + urllib.parse.urlencode(params)
    with urllib.request.urlopen(url, timeout=30) as response:
        body = json.load(response)
    if body["status"] != "success":
        raise RuntimeError(body)
    return body["data"]["result"]


def series(query, start, end):
    """One time series as [(t, value)], summed if the query returns several."""
    result = get("query_range", query=f"sum({query})", start=start, end=end, step=STEP)
    return [(float(t), float(v)) for t, v in result[0]["values"]] if result else []


def instant(query, t):
    result = get("query", query=query, time=t)
    return float(result[0]["value"][1]) if result else float("nan")


def first(points, condition, after=0.0):
    return next((t for t, v in points if t >= after and condition(v)), None)


def epoch(iso):
    return datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()


def fmt_s(seconds):
    return "–" if seconds is None else f"{seconds:.0f} s"


def fmt_min(seconds):
    return "–" if seconds is None else f"{seconds / 60:.1f} min"


def run_figures(name, cluster, start_iso):
    t0 = epoch(start_iso)
    end = t0 + 25 * 60
    c = f'cluster="{cluster}"'

    queue = series(f'redis_key_size{{{c},key="hba:jobs"}}', t0, end)
    # Ready = warmed up and scraped: a worker reports hba_worker_info only after its warm-up
    ready = series(f"count(hba_worker_info{{{c}}})", t0, end)
    desired = series(DESIRED[cluster], t0, end)
    scheduled = series(SCHEDULED[cluster], t0, end)

    t_desired6 = first(desired, lambda v: v >= 6)
    t_ready6 = first(ready, lambda v: v >= 6)
    warmup_peak = max((v for t, v in queue if t <= t0 + 150), default=float("nan"))
    peak_t, peak_v = max(queue, key=lambda p: p[1])
    t_empty = first(queue, lambda v: v == 0, after=peak_t)
    t_back1 = first(scheduled, lambda v: v <= 1, after=t_empty or peak_t)

    # Saturated: from t+300 s (arrivals above capacity, all six busy) to the queue peak.
    # Counter differences, not increase(): no worker starts or stops in either window
    done = f'sum(hba_jobs_completed_total{{{c},status="done"}})'
    sat_from, sat_to = t0 + 300, peak_t
    sat_rate = (instant(done, sat_to) - instant(done, sat_from)) / (sat_to - sat_from)
    drain_rate = ((instant(done, t_empty) - instant(done, peak_t)) / (t_empty - peak_t)
                  if t_empty else float("nan"))

    window = f"{int(sat_to - sat_from)}s"
    hist = f"sum by (le) (increase(hba_job_duration_seconds_bucket{{{c}}}[{window}]))"
    p50 = instant(f"histogram_quantile(0.5, {hist})", sat_to)
    p95 = instant(f"histogram_quantile(0.95, {hist})", sat_to)

    workers = f'{c},node_role="worker"'
    cpu = instant(f'1 - avg(rate(node_cpu_seconds_total{{{workers},mode="idle"}}[{window}]))', sat_to)
    steal = instant(f'avg(rate(node_cpu_seconds_total{{{workers},mode="steal"}}[{window}]))', sat_to)

    return {
        "run": name,
        "desired 6 after k6 start": fmt_s(t_desired6 and t_desired6 - t0),
        "6 ready (warmed, scraped) after k6 start": fmt_s(t_ready6 and t_ready6 - t0),
        "warm-up queue peak (first 150 s)": f"{warmup_peak:.0f}",
        "queue peak": f"{peak_v:.0f} (t+{peak_t - t0:.0f} s)",
        "throughput saturated (t+300 s → peak)": f"{sat_rate:.2f} jobs/s",
        "throughput during drain": f"{drain_rate:.2f} jobs/s",
        "drain: peak → empty": fmt_s(t_empty and t_empty - peak_t),
        "k6 start → empty queue": fmt_min(t_empty and t_empty - t0),
        "k6 start → back at 1 replica": fmt_min(t_back1 and t_back1 - t0),
        "job duration saturated, median / p95": f"{p50:.2f} / {p95:.2f} s",
        "worker CPU busy saturated / steal": f"{cpu * 100:.1f} % / {steal * 100:.2f} %",
    }


def print_table():
    rows = [run_figures(*run) for run in RUNS]
    keys = list(rows[0])[1:]
    width = max(len(k) for k in keys)
    print(f"{'':{width}}  " + "  ".join(f"{r['run']:>18}" for r in rows))
    for key in keys:
        print(f"{key:{width}}  " + "  ".join(f"{r[key]:>18}" for r in rows))


def print_isolation():
    """One line per run, all conditions: the main runs and the §4.8 experiments."""
    columns = {
        "desired 6 after k6 start": "decides 6",
        "6 ready (warmed, scraped) after k6 start": "6 ready",
        "warm-up queue peak (first 150 s)": "warm-up peak",
        "queue peak": "queue peak",
        "throughput saturated (t+300 s → peak)": "saturated",
        "k6 start → empty queue": "to empty",
        "job duration saturated, median / p95": "job med / p95",
    }
    print(f"{'':16}" + "".join(f"{label:>16}" for label in columns.values()))
    for run in RUNS + ISOLATION_RUNS:
        row = run_figures(*run)
        cells = (row[key].split(" (t+")[0].replace(" jobs/s", "") for key in columns)
        print(f"{row['run']:16}" + "".join(f"{cell:>16}" for cell in cells))


def group_activities(cluster):
    """Launch start and termination end per instance, from the Auto Scaling group's own history
    (kept for six weeks): the moment AWS was asked for a node, and when it stopped costing."""
    command = ["aws", "autoscaling", "describe-scaling-activities", "--region", AWS_REGION,
               "--auto-scaling-group-name", BURST_GROUP[cluster], "--output", "json"]
    activities = json.loads(subprocess.run(command, capture_output=True, text=True, check=True).stdout)
    launched, ended = {}, {}
    for activity in activities["Activities"]:
        instance = activity["Description"].rsplit(" ", 1)[-1]
        if activity["Description"].startswith("Launching"):
            launched[instance] = epoch(activity["StartTime"])
        elif activity["Description"].startswith("Terminating") and activity.get("EndTime"):
            ended[instance] = epoch(activity["EndTime"])
    return launched, ended


def burst_figures(name, cluster, start_iso, activities):
    t0 = epoch(start_iso)
    # A burst run's queue is empty after about 10 minutes, and runs started 18 minutes apart:
    # a longer window would take in the next run's queue. Node lifetimes come from AWS, unwindowed
    end = t0 + 15 * 60
    c = f'cluster="{cluster}"'

    queue = series(f'redis_key_size{{{c},key="hba:jobs"}}', t0, end)
    workers = series(f"count(hba_worker_info{{{c}}})", t0, end)
    busy = series(f"hba_jobs_in_progress{{{c}}}", t0, end)
    aws_busy = series(f'hba_jobs_in_progress{{{c},tier="aws"}}', t0, end)
    aws_done = series(f'hba_jobs_completed_total{{{c},tier="aws",status="done"}}', t0, end)

    launched, ended = activities
    instances = {i: s for i, s in launched.items() if t0 <= s <= end}
    t_node = min(instances.values(), default=None)
    t_aws_job = first(aws_done, lambda v: v >= 1)
    t_aws4 = first(aws_busy, lambda v: v >= 4)
    t_busy10 = first(busy, lambda v: v >= 10)
    peak_t, peak_v = max(queue, key=lambda p: p[1])
    t_empty = first(queue, lambda v: v == 0, after=peak_t)

    # Capacity while all ten workers are busy: from the first moment all ten work to the empty queue
    def done(tier):
        return f'sum(hba_jobs_completed_total{{{c},status="done",tier="{tier}"}})'

    def rate(tier):
        return (instant(done(tier), t_empty) - instant(done(tier), t_busy10)) / (t_empty - t_busy10)

    onprem_rate, aws_rate = rate("onprem"), rate("aws")

    window = f"{int(t_empty - t0)}s"

    def quantile(q, tier):
        hist = f'sum by (le) (increase(hba_job_duration_seconds_bucket{{{c},tier="{tier}"}}[{window}]))'
        return instant(f"histogram_quantile({q}, {hist})", t_empty)

    # Instance time: launch start to termination end, and the part of it after the queue was empty
    total = sum(ended[i] - s for i, s in instances.items() if i in ended)
    after = sum(max(0.0, ended[i] - max(s, t_empty)) for i, s in instances.items() if i in ended)

    return {
        "run": name,
        "first node requested": fmt_s(t_node and t_node - t0),
        "time-to-capacity (request → AWS job)": fmt_s(t_aws_job and t_node and t_aws_job - t_node),
        "all 4 AWS workers busy": fmt_s(t_aws4 and t_aws4 - t0),
        "most workers": f"{max(v for _, v in workers):.0f}",
        "throughput, 10 busy (on-prem / AWS)": f"{onprem_rate + aws_rate:.2f} ({onprem_rate:.2f} / {aws_rate:.2f})",
        "queue peak": f"{peak_v:.0f} (t+{peak_t - t0:.0f} s)",
        "k6 start → empty queue": fmt_min(t_empty and t_empty - t0),
        "job on-prem, median / p95": f"{quantile(0.5, 'onprem'):.2f} / {quantile(0.95, 'onprem'):.2f} s",
        "job AWS, median / p95": f"{quantile(0.5, 'aws'):.2f} / {quantile(0.95, 'aws'):.2f} s",
        "instance time (after empty)": f"{total / 60:.1f} min ({after / 60:.1f})",
        "burst nodes": f"{len(instances)}",
    }


def print_burst():
    activities = {cluster: group_activities(cluster) for cluster in {r[1] for r in BURST_RUNS}}
    rows = [burst_figures(*run, activities[run[1]]) for run in BURST_RUNS]
    keys = list(rows[0])[1:]
    width = max(len(k) for k in keys)
    print(f"{'':{width}}  " + "  ".join(f"{r['run']:>20}" for r in rows))
    for key in keys:
        print(f"{key:{width}}  " + "  ".join(f"{r[key]:>20}" for r in rows))


def print_idle(cluster, start_iso, minutes=10):
    """CPU busy per VM while the cluster is idle: queue 0, one idle worker replica."""
    t1 = epoch(start_iso) + minutes * 60
    # rate() divides by the whole window: one that hasn't ended yet reads as busy (half a window
    # of data looks like ~50 % CPU). Refuse it rather than print a wrong number
    if t1 > datetime.now().timestamp():
        sys.exit(f"the window ends in {t1 - datetime.now().timestamp():.0f} s; run again after it")
    window = f"{minutes * 60}s"
    c = f'cluster="{cluster}"'
    queue_max = instant(f'max_over_time(redis_key_size{{{c},key="hba:jobs"}}[{window}])', t1)
    rows = get("query", time=t1, query=(
        f'100 * (1 - avg by (instance, node_role) (rate(node_cpu_seconds_total{{{c},mode="idle"}}[{window}])))'))
    print(f"{cluster} idle from {start_iso} for {minutes} min (queue max {queue_max:.0f}): CPU busy per VM (2 vCPU)")
    for row in sorted(rows, key=lambda r: r["metric"]["instance"]):
        print(f"  {row['metric']['instance']:10} {row['metric']['node_role']:8} {float(row['value'][1]):5.2f} %")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "idle":
        print_idle(sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 10)
    elif len(sys.argv) > 1 and sys.argv[1] == "isolation":
        print_isolation()
    elif len(sys.argv) > 1 and sys.argv[1] == "burst":
        print_burst()
    else:
        print_table()
