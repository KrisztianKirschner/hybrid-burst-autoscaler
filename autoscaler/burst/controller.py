"""Burst controller: adds AWS nodes when the scheduler can't place worker tasks, and removes
them when their tasks fit back on-prem (docs/burst.md, ADR 0009).

Every scan_interval seconds: read the cluster from its orchestrator (Docker or Kubernetes) and the
Auto Scaling group from AWS, ask decision.decide() what to do, and do it. A failed read skips the
iteration; a failed action is retried by the next one, because every decision starts again from
the current state.

Settings come from burst-policy.yml (path in HBA_POLICY_FILE); its `orchestrator` field picks the
adapter, Swarm when it's absent. The controller's own metrics are served on :8000/metrics for the
cluster's Prometheus agent.
"""

import logging
import os
import signal
import threading
import time

import boto3
import docker
import requests
import yaml
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError
from docker.errors import DockerException
from prometheus_client import Counter, Gauge, start_http_server

from aws_client import BurstGroup
from decision import Cluster, Policy, decide, qualifying_pending
from k8s_adapter import KubernetesAdapter
from swarm_adapter import SwarmAdapter

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("burst-controller")

with open(os.environ.get("HBA_POLICY_FILE", "burst-policy.yml")) as f:
    settings = yaml.safe_load(f)

ASG_NAME = settings["asg_name"]
AWS_REGION = settings["aws_region"]
ORCHESTRATOR = settings.get("orchestrator", "swarm")  # swarm or kubernetes
SCAN_INTERVAL = settings["scan_interval"]  # seconds
POLICY = Policy(
    min_nodes=settings["min_nodes"],
    max_nodes=settings["max_nodes"],
    new_pod_scale_up_delay=settings["new_pod_scale_up_delay"],
    scale_down_unneeded_time=settings["scale_down_unneeded_time"],
    max_node_provision_time=settings["max_node_provision_time"],
    replicas_per_node=settings["replicas_per_node"],
    stranded_task_grace=settings["stranded_task_grace"],
)
METRICS_PORT = 8000

AWS_ERRORS = (BotoCoreError, ClientError)

# What a failed orchestrator call raises, and its label in hba_burst_errors ("docker" as before on Swarm)
if ORCHESTRATOR == "kubernetes":
    from kubernetes import client as kube_client
    from kubernetes import config as kube_config
    from kubernetes.client.exceptions import ApiException
    from urllib3.exceptions import HTTPError

    ORCHESTRATOR_ERRORS = (ApiException, HTTPError)
    ERROR_SOURCE = "kubernetes"
else:
    ORCHESTRATOR_ERRORS = (DockerException, requests.RequestException)
    ERROR_SOURCE = "docker"

PENDING = Gauge("hba_burst_pending_tasks", "Tasks Pending long enough for lack of resources")
DESIRED = Gauge("hba_burst_nodes_desired", "Desired capacity of the Auto Scaling group")
READY = Gauge("hba_burst_nodes_ready", "Burst nodes ready and not draining")
LAST_JOIN = Gauge("hba_burst_last_join_seconds", "Newest burst node: launch to cluster join")
SCALE_EVENTS = Counter("hba_burst_scale_events", "Changes to the burst capacity", ["direction"])
RESPAWNED = Counter("hba_burst_respawned_tasks", "Stranded Pending tasks re-created")
ERRORS = Counter("hba_burst_errors", "Failed reads and actions", ["source"])

# Set by SIGTERM (Swarm stopping the task) or SIGINT (Ctrl+C); also wakes the sleep
stop = threading.Event()


def read_secret(env_name):
    """Swarm secrets are files. With the variable unset, boto3 uses its default credential chain."""
    path = os.environ.get(env_name)
    if not path:
        return None
    with open(path) as secret:
        return secret.read().strip()


def attempt(source, errors, description, action, *args):
    """Runs one action. Returns True on success; a failure is retried by the next iteration."""
    try:
        action(*args)
    except errors as err:
        log.warning("%s failed: %s", description, err)
        ERRORS.labels(source=source).inc()
        return False
    log.info(description)
    return True


def publish(cluster):
    PENDING.set(qualifying_pending(cluster, POLICY))
    DESIRED.set(cluster.desired_capacity)
    READY.set(sum(1 for n in cluster.nodes if n.ready and not n.draining))
    launched = {i.id: i.launched_at for i in cluster.instances}
    joined = [n for n in cluster.nodes if n.instance_id in launched]
    if joined:
        newest = max(joined, key=lambda n: n.joined_at)
        LAST_JOIN.set(newest.joined_at - launched[newest.instance_id])


def apply(decision, cluster_api, group):
    """Terminate comes before set_desired: its decrement must not undo the new target.
    cluster_api is the orchestrator's adapter (SwarmAdapter or KubernetesAdapter)."""
    for node_id in decision.remove:
        attempt(ERROR_SOURCE, ORCHESTRATOR_ERRORS, f"removed node {node_id}", cluster_api.remove, node_id)
    for instance_id, reason in (decision.terminate or {}).items():
        description = f"terminated instance {instance_id} ({reason})"
        if attempt("aws", AWS_ERRORS, description, group.terminate, instance_id):
            SCALE_EVENTS.labels(direction="down").inc()
    if decision.set_desired is not None:
        description = f"set desired capacity to {decision.set_desired}"
        if attempt("aws", AWS_ERRORS, description, group.set_desired, decision.set_desired):
            SCALE_EVENTS.labels(direction="up").inc()
    if decision.drain is not None:
        description = f"draining node {decision.drain}"
        attempt(ERROR_SOURCE, ORCHESTRATOR_ERRORS, description, cluster_api.drain, decision.drain)
    # Only Swarm strands Pending tasks (docs/burst.md §3.1); Kubernetes retries them by itself
    if decision.respawn and getattr(cluster_api, "NEEDS_RESPAWN", True):
        description = f"re-created {decision.respawn} stranded Pending task(s)"
        if attempt(ERROR_SOURCE, ORCHESTRATOR_ERRORS, description, cluster_api.respawn, decision.respawn):
            RESPAWNED.inc(decision.respawn)


def scan(cluster_api, group, timers):
    """One iteration. timers is (unneeded_since, stranded_since); returns them for the next one."""
    unneeded_since, stranded_since = timers
    now = time.time()
    try:
        view = cluster_api.read(now)
    except (*ORCHESTRATOR_ERRORS, KeyError) as err:
        log.warning("cannot read the cluster: %s", err)
        ERRORS.labels(source=ERROR_SOURCE).inc()
        return timers
    try:
        desired_capacity, instances = group.read()
    except (*AWS_ERRORS, LookupError) as err:
        log.warning("cannot read the Auto Scaling group: %s", err)
        ERRORS.labels(source="aws").inc()
        return timers

    cluster = Cluster(view.pending, view.onprem_free_slots, view.nodes, desired_capacity, instances)
    decision = decide(cluster, POLICY, unneeded_since, now, stranded_since)
    publish(cluster)
    log.info(
        "pending=%s qualifying=%s free_onprem=%s nodes=%s instances=%s desired=%s",
        len(cluster.pending), qualifying_pending(cluster, POLICY), cluster.onprem_free_slots,
        len(cluster.nodes), len(cluster.instances), desired_capacity,
    )
    apply(decision, cluster_api, group)
    return decision.unneeded_since, decision.stranded_since


def handle_signal(signum, frame):
    log.info("received %s, stopping after this iteration", signal.Signals(signum).name)
    stop.set()


def main():
    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    session = boto3.Session(
        aws_access_key_id=read_secret("HBA_AWS_ACCESS_KEY_ID_FILE"),
        aws_secret_access_key=read_secret("HBA_AWS_SECRET_ACCESS_KEY_FILE"),
        region_name=AWS_REGION,
    )
    # Short timeouts: a hung AWS call must not hold up the loop for a minute
    aws_config = Config(connect_timeout=5, read_timeout=10, retries={"mode": "standard", "max_attempts": 3})
    group = BurstGroup(
        session.client("autoscaling", config=aws_config),
        session.client("ec2", config=aws_config),
        ASG_NAME,
    )
    if ORCHESTRATOR == "kubernetes":
        # The pod's service account; its RBAC allows reading nodes and pods, cordoning,
        # evicting worker pods and deleting nodes (stacks/k8s/burst.yml)
        kube_config.load_incluster_config()
        cluster_api = KubernetesAdapter(kube_client.CoreV1Api(), settings["namespace"], settings["worker_label"])
        target = f"{settings['namespace']}/{settings['worker_label']}"
    else:
        cluster_api = SwarmAdapter(docker.from_env(), settings["service"])
        target = settings["service"]

    start_http_server(METRICS_PORT)
    log.info("started: orchestrator=%s workers=%s asg=%s policy=%s", ORCHESTRATOR, target, ASG_NAME, POLICY)

    timers = ({}, None)
    while not stop.is_set():
        timers = scan(cluster_api, group, timers)
        stop.wait(SCAN_INTERVAL)

    log.info("stopped")


if __name__ == "__main__":
    main()
