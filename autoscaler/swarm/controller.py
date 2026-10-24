"""Swarm autoscaler: scales the worker service on the length of the Redis job queue.

Every poll_interval seconds:
  1. read the queue length from Prometheus (redis_key_size{key="hba:jobs"})
  2. read the worker service's current replica count from the Docker API
  3. desired = ceil(depth / jobs_per_replica), clamped to [min_replicas, max_replicas]
  4. scale up to desired once the scale-up cooldown has passed; scale down only to the
     highest desired of the last scale_down_stabilization seconds (the Kubernetes HPA's
     scale-down stabilization window, so both orchestrators scale down by the same rule)

Settings come from policy.yml (path in HBA_POLICY_FILE). The controller's own
metrics are served on :8000/metrics for the in-swarm Prometheus agent.
"""

import logging
import math
import os
import signal
import threading
import time
from collections import deque

import docker
import requests
import yaml
from docker.errors import APIError, DockerException
from prometheus_client import Counter, Gauge, start_http_server

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("controller")

with open(os.environ.get("HBA_POLICY_FILE", "policy.yml")) as f:
    policy = yaml.safe_load(f)

PROM_URL = policy["prometheus_url"].rstrip("/") + "/api/v1/query"
SERVICE = policy["service"]
QUERY = policy["query"]
JOBS_PER_REPLICA = policy["jobs_per_replica"]
MIN_REPLICAS = policy["min_replicas"]
MAX_REPLICAS = policy["max_replicas"]
POLL_INTERVAL = policy["poll_interval"]        # seconds
UP_COOLDOWN = policy["scale_up_cooldown"]      # seconds since the last scale-up
DOWN_WINDOW = policy["scale_down_stabilization"]  # seconds of desired counts a scale-down looks back over
METRICS_PORT = 8000

DEPTH = Gauge("hba_autoscaler_queue_depth", "Queue length read from Prometheus")
CURRENT = Gauge("hba_autoscaler_current_replicas", "Replicas Swarm reports for the worker service")
DESIRED = Gauge("hba_autoscaler_desired_replicas", "Replicas the controller wants")
SCALINGS = Counter("hba_autoscaler_scalings", "Scale operations sent to Swarm", ["direction"])
ERRORS = Counter("hba_autoscaler_errors", "Loop iterations skipped because of an error", ["source"])

# Set by SIGTERM (Swarm stopping the task) or SIGINT (Ctrl+C); also wakes the sleep
stop = threading.Event()


class PrometheusError(Exception):
    pass


def read_queue_depth():
    resp = requests.get(PROM_URL, params={"query": QUERY}, timeout=5)
    resp.raise_for_status()
    body = resp.json()
    if body.get("status") != "success":
        raise PrometheusError(f"query failed: {body}")
    results = body["data"]["result"]
    # No series is "unknown", not 0: scaling to min on a missing metric would be wrong
    if not results:
        raise PrometheusError(f"no series for {QUERY}")
    return float(results[0]["value"][1])


def desired_replicas(depth):
    wanted = math.ceil(depth / JOBS_PER_REPLICA)
    return max(MIN_REPLICAS, min(MAX_REPLICAS, wanted))


def scale(service, current, desired):
    """Returns True if Swarm accepted the new replica count."""
    try:
        service.scale(desired)
    except APIError as err:
        # Usually "update out of sequence": the service changed since we read it. Retried next loop.
        log.warning("scaling %s from %s to %s failed: %s", SERVICE, current, desired, err)
        ERRORS.labels(source="docker").inc()
        return False
    log.info("scaled %s from %s to %s", SERVICE, current, desired)
    return True


def handle_signal(signum, frame):
    log.info("received %s, stopping after this iteration", signal.Signals(signum).name)
    stop.set()


def main():
    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    client = docker.from_env()
    start_http_server(METRICS_PORT)
    log.info(
        "started: service=%s query=%s replicas=%s..%s jobs_per_replica=%s poll=%ss down_window=%ss",
        SERVICE, QUERY, MIN_REPLICAS, MAX_REPLICAS, JOBS_PER_REPLICA, POLL_INTERVAL, DOWN_WINDOW,
    )

    last_up = float("-inf")  # time.monotonic() of the last scale-up
    recent = deque()         # (time.monotonic(), desired) of the last DOWN_WINDOW seconds

    while not stop.is_set():
        try:
            depth = read_queue_depth()
        except (requests.RequestException, PrometheusError, KeyError, IndexError, ValueError) as err:
            # Without a queue length, leave the replicas as they are
            log.warning("no queue depth, keeping current replicas: %s", err)
            ERRORS.labels(source="prometheus").inc()
            stop.wait(POLL_INTERVAL)
            continue
        DEPTH.set(depth)

        try:
            service = client.services.get(SERVICE)
            current = service.attrs["Spec"]["Mode"]["Replicated"]["Replicas"]
        except (DockerException, KeyError) as err:
            log.warning("cannot read service %s: %s", SERVICE, err)
            ERRORS.labels(source="docker").inc()
            stop.wait(POLL_INTERVAL)
            continue

        desired = desired_replicas(depth)
        CURRENT.set(current)
        DESIRED.set(desired)

        now = time.monotonic()
        recent.append((now, desired))
        while recent[0][0] < now - DOWN_WINDOW:
            recent.popleft()
        # Scale down only to the highest count wanted within the window: a queue that empties
        # for a moment (arrivals between the capacity of 1 and of all replicas) must not undo a
        # scale-up, or the replicas oscillate. A 60 s cooldown did exactly that (docs/loadtest.md §4)
        stable = max(d for _, d in recent)

        # Scale up quickly; scale down slowly
        if desired > current and now - last_up >= UP_COOLDOWN:
            if scale(service, current, desired):
                SCALINGS.labels(direction="up").inc()
                last_up = now
        elif stable < current:
            if scale(service, current, stable):
                SCALINGS.labels(direction="down").inc()
        else:
            log.info("depth=%s current=%s desired=%s stable=%s", depth, current, desired, stable)

        stop.wait(POLL_INTERVAL)

    log.info("stopped")


if __name__ == "__main__":
    main()
