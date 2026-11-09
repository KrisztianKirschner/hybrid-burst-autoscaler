"""Swarm adapter: turns the Docker API's nodes and tasks into the decision function's terms,
and carries out the Swarm side of a scale-down. A Kubernetes adapter offers the same methods."""

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
