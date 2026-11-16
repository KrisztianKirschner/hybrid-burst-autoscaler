"""Swarm adapter: turns the Docker API's nodes and tasks into the decision function's terms,
and carries out the Swarm side of a scale-down. A Kubernetes adapter offers the same methods."""

import copy
import time
from dataclasses import dataclass
from datetime import datetime

from decision import Node, PendingTask

BURST_PREFIX = "burst-"  # the boot script sets the hostname to burst-<instance id>
NOT_PLACED_YET = {"new", "allocated", "pending"}
FINISHED = {"complete", "shutdown", "failed", "rejected", "remove", "orphaned"}


@dataclass(frozen=True)
class SwarmView:
    pending: tuple[PendingTask, ...]
    onprem_free_slots: int
    nodes: tuple[Node, ...]


def _epoch(timestamp):
    return datetime.fromisoformat(timestamp).timestamp()


def build_view(nodes, tasks, reservation_nano_cpus, now):
    """nodes and tasks are the raw dicts of the Docker API."""
    pending = []
    tasks_on = {}
    for task in tasks:
        state = task["Status"]["State"]
        if task.get("NodeID"):
            # Stopping tasks count too: the instance must outlive their grace period
            if state not in FINISHED:
                tasks_on[task["NodeID"]] = tasks_on.get(task["NodeID"], 0) + 1
        elif task["DesiredState"] == "running" and state in NOT_PLACED_YET:
            pending.append(PendingTask(
                age=now - _epoch(task["CreatedAt"]),
                # e.g. "no suitable node (insufficient resources on 3 nodes)"
                lacks_resources="insufficient resources" in task["Status"].get("Err", ""),
            ))

    free_slots = 0
    burst_nodes = []
    for node in nodes:
        hostname = node["Description"]["Hostname"]
        ready = node["Status"]["State"] == "ready"
        availability = node["Spec"]["Availability"]
        on_node = tasks_on.get(node["ID"], 0)
        if hostname.startswith(BURST_PREFIX):
            burst_nodes.append(Node(
                id=node["ID"],
                instance_id=hostname.removeprefix(BURST_PREFIX),
                ready=ready,
                draining=availability == "drain",
                tasks=on_node,
                joined_at=_epoch(node["CreatedAt"]),
            ))
        elif node["Spec"]["Role"] == "worker" and ready and availability == "active":
            slots = node["Description"]["Resources"]["NanoCPUs"] // reservation_nano_cpus
            free_slots += max(0, slots - on_node)

    return SwarmView(tuple(pending), free_slots, tuple(burst_nodes))


class SwarmAdapter:
    def __init__(self, client, service):
        self.client = client
        self.service = service

    def read(self, now):
        service = self.client.services.get(self.service)
        reservation = service.attrs["Spec"]["TaskTemplate"]["Resources"]["Reservations"]["NanoCPUs"]
        nodes = [node.attrs for node in self.client.nodes.list()]
        return build_view(nodes, service.tasks(), reservation, now)

    def drain(self, node_id):
        node = self.client.nodes.get(node_id)
        spec = node.attrs["Spec"]
        spec["Availability"] = "drain"
        node.update(spec)

    def remove(self, node_id):
        self.client.nodes.get(node_id).remove()

    def respawn(self, count):
        """Re-creates `count` stranded Pending tasks. Swarm's scheduler re-queues a task it can't
        place only while the task belongs to the service's current revision, and every scale
        makes a new revision, so a task left over from an earlier scale step is never retried,
        not even when a node joins. Scaling down removes unassigned tasks first; scaling back up
        creates them again under the current revision, and the scheduler places them."""
        replicas = self.client.api.inspect_service(self.service)["Spec"]["Mode"]["Replicated"]["Replicas"]
        self._set_replicas(replicas - count)
        self._wait_for_tasks(replicas - count)
        self._set_replicas(replicas)

    def _set_replicas(self, replicas, attempts=5):
        """Sends the service's current spec back with only the replica count changed, as the
        Docker CLI does. docker-py's scale() reads the spec with defaults filled in
        (insert_defaults) and sends that; after a `docker stack deploy` Swarm takes it for a new
        task template and restarts every task (seen on 2026-11-16, docs/burst.md §3.1)."""
        api = self.client.api
        for attempt in range(attempts):
            current = api.inspect_service(self.service)
            spec = current["Spec"]
            mode = copy.deepcopy(spec["Mode"])
            mode["Replicated"]["Replicas"] = replicas
            try:
                api.update_service(
                    current["ID"], current["Version"]["Index"],
                    task_template=spec["TaskTemplate"], name=spec["Name"], labels=spec.get("Labels"),
                    mode=mode, update_config=spec.get("UpdateConfig"),
                    rollback_config=spec.get("RollbackConfig"), endpoint_spec=spec.get("EndpointSpec"),
                )
                return
            # The version moved between inspect and update (the replica controller, or Swarm's own
            # status writes): read again. Matched on the text, so this module needs no docker import.
            except Exception as err:  # noqa: BLE001
                if "out of sequence" not in str(err) or attempt == attempts - 1:
                    raise

    def _wait_for_tasks(self, replicas, timeout=15):
        """Waits until Swarm has marked the extra tasks for removal. Scaling back up before that
        could leave the old, stranded tasks in place instead of creating new ones."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            running = self.client.api.tasks(filters={"service": self.service, "desired-state": "running"})
            if len(running) <= replicas:
                return
            time.sleep(0.5)
