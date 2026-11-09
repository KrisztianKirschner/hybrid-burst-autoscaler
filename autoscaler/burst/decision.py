"""What the burst tier should do next. No I/O and nothing Swarm-specific, so the same
function serves both orchestrators (docs/burst.md).

Scale-up: tasks that have waited too long for lack of resources raise the desired capacity to
ceil((waiting tasks + tasks on burst nodes) / replicas_per_node). It is never lowered here,
because lowering it would let AWS choose which instance to terminate.

Scale-down: a node whose tasks fit elsewhere for scale_down_unneeded_time is drained, its
instance terminated and the node removed, one node at a time. Each step is derived from the
cluster's current state, so a restarted controller carries on where the last one stopped.
"""

import math
from dataclasses import dataclass


@dataclass(frozen=True)
class Policy:
    min_nodes: int
    max_nodes: int
    new_pod_scale_up_delay: float
    scale_down_unneeded_time: float
    max_node_provision_time: float
    replicas_per_node: int


@dataclass(frozen=True)
class PendingTask:
    age: float             # seconds since the task was created
    lacks_resources: bool  # False: Pending for another reason, which a new node wouldn't fix


@dataclass(frozen=True)
class Node:
    """A burst node the orchestrator knows about."""
    id: str
    instance_id: str
    ready: bool
    draining: bool
    tasks: int              # worker tasks that haven't finished yet, including ones stopping
    joined_at: float = 0.0  # epoch seconds; only used for the join-time metric


@dataclass(frozen=True)
class Instance:
    """An instance the Auto Scaling group is launching or running."""
    id: str
    launched_at: float  # epoch seconds


@dataclass(frozen=True)
class Cluster:
    pending: tuple[PendingTask, ...]
    onprem_free_slots: int
    nodes: tuple[Node, ...]
    desired_capacity: int
    instances: tuple[Instance, ...]


@dataclass(frozen=True)
class Decision:
    unneeded_since: dict[str, float]  # node id -> epoch seconds; pass to the next decide()
    set_desired: int | None = None
    drain: str | None = None          # node id
    terminate: dict[str, str] | None = None  # instance id -> reason
    remove: tuple[str, ...] = ()      # node ids


def qualifying_pending(cluster, policy):
    return sum(
        1 for task in cluster.pending
        if task.lacks_resources and task.age >= policy.new_pod_scale_up_delay
    )


def _scale_up_target(cluster, policy):
    """The new desired capacity, or None to leave it. Booting instances are already part of
    the desired capacity, so they aren't launched twice."""
    waiting = qualifying_pending(cluster, policy)
    target = policy.min_nodes
    if waiting:
        running = sum(n.tasks for n in cluster.nodes if n.ready and not n.draining)
        needed = math.ceil((waiting + running) / policy.replicas_per_node)
        target = max(policy.min_nodes, min(policy.max_nodes, needed))
    return target if target > cluster.desired_capacity else None


def decide(cluster, policy, unneeded_since, now):
    in_group = {i.id for i in cluster.instances}
    joined = {n.instance_id for n in cluster.nodes}
    # A node whose instance is already terminating is no longer ours to judge
    nodes = [n for n in cluster.nodes if n.instance_id in in_group]
    active = [n for n in nodes if n.ready and not n.draining]

    def free_slots_besides(node):
        others = sum(max(0, policy.replicas_per_node - n.tasks) for n in active if n is not node)
        return cluster.onprem_free_slots + others

    # Any Pending task blocks scale-down, even one too young to trigger a scale-up
    unneeded = {}
    for node in nodes:
        fits = not cluster.pending and node.tasks <= free_slots_besides(node)
        if not node.draining and (not node.ready or fits):
            unneeded[node.id] = unneeded_since.get(node.id, now)

    def is_due(node):
        return node.id in unneeded and now - unneeded[node.id] >= policy.scale_down_unneeded_time

    terminate = {}
    for instance in cluster.instances:
        if instance.id not in joined and now - instance.launched_at >= policy.max_node_provision_time:
            terminate[instance.id] = "never joined"
    for node in nodes:
        if node.draining and (node.tasks == 0 or not node.ready):
            terminate[node.instance_id] = "drained"
        elif not node.ready and is_due(node):
            terminate[node.instance_id] = "not ready"  # drained tasks can't stop on a dead node

    drain = None
    drain_allowed = (
        cluster.desired_capacity > policy.min_nodes
        and not any(n.draining for n in nodes)
    )
    candidates = [n for n in active if is_due(n)]
    if drain_allowed and candidates:
        drain = min(candidates, key=lambda n: n.tasks).id

    return Decision(
        unneeded_since=unneeded,
        set_desired=_scale_up_target(cluster, policy),
        drain=drain,
        terminate=terminate or None,
        remove=tuple(n.id for n in cluster.nodes if not n.ready and n.instance_id not in in_group),
    )
