"""Kubernetes adapter: turns the Kubernetes API's nodes and pods into the decision function's terms,
and carries out the Kubernetes side of a scale-down. Offers the same methods as SwarmAdapter.

It takes the API clients as arguments and works on the API's JSON as plain dicts, so neither this
module nor its tests need the kubernetes library; controller.py creates the clients."""

from dataclasses import dataclass
from datetime import datetime

from decision import Node, PendingTask

BURST_PREFIX = "burst-"  # the boot script sets the hostname, and so the node name, to burst-<instance id>
CONTROL_PLANE_LABEL = "node-role.kubernetes.io/control-plane"
FINISHED = {"Succeeded", "Failed"}


@dataclass(frozen=True)
class KubernetesView:
    pending: tuple[PendingTask, ...]
    onprem_free_slots: int
    nodes: tuple[Node, ...]


def _epoch(timestamp):
    return datetime.fromisoformat(timestamp.replace("Z", "+00:00")).timestamp()


def millicores(quantity):
    """A CPU quantity as the API writes it ("2", "1", "900m", "0.5") in millicores."""
    text = str(quantity)
    return int(text[:-1]) if text.endswith("m") else round(float(text) * 1000)


def _cpu_request(pod):
    return sum(
        millicores(c.get("resources", {}).get("requests", {}).get("cpu", "0"))
        for c in pod["spec"].get("containers", [])
    )


def _condition(obj, kind):
    return next((c for c in obj.get("status", {}).get("conditions") or [] if c.get("type") == kind), {})


def build_view(nodes, pods, is_worker, now):
    """nodes and pods are the API's JSON objects (camelCase keys). pods are all unfinished pods of
    the cluster: the other pods' CPU requests use up worker slots too (CoreDNS, if it ever moves to
    a worker node, takes 100m). is_worker(pod) picks the workload's own pods."""
    pending = []
    workers_on = {}
    requested_on = {}
    request = None
    for pod in pods:
        if pod["status"].get("phase") in FINISHED:
            continue
        node_name = pod["spec"].get("nodeName")
        if not is_worker(pod):
            if node_name:
                requested_on[node_name] = requested_on.get(node_name, 0) + _cpu_request(pod)
            continue
        request = request or _cpu_request(pod)
        if node_name:
            # Terminating pods count too: the instance must outlive their grace period
            workers_on[node_name] = workers_on.get(node_name, 0) + 1
        elif not pod["metadata"].get("deletionTimestamp"):
            scheduled = _condition(pod, "PodScheduled")
            pending.append(PendingTask(
                age=now - _epoch(pod["metadata"]["creationTimestamp"]),
                # e.g. "0/4 nodes are available: 1 node(s) had untolerated taint ..., 3 Insufficient cpu."
                lacks_resources=(
                    scheduled.get("reason") == "Unschedulable"
                    and "Insufficient cpu" in scheduled.get("message", "")
                ),
            ))

    free_slots = 0
    burst_nodes = []
    for node in nodes:
        name = node["metadata"]["name"]
        ready = _condition(node, "Ready").get("status") == "True"
        cordoned = bool(node["spec"].get("unschedulable"))
        on_node = workers_on.get(name, 0)
        if name.startswith(BURST_PREFIX):
            burst_nodes.append(Node(
                id=name,
                instance_id=name.removeprefix(BURST_PREFIX),
                ready=ready,
                draining=cordoned,  # drain() cordons first, like Swarm's availability "drain"
                tasks=on_node,
                joined_at=_epoch(node["metadata"]["creationTimestamp"]),
            ))
        elif CONTROL_PLANE_LABEL not in node["metadata"].get("labels", {}) and ready and not cordoned and request:
            usable = millicores(node["status"]["allocatable"]["cpu"]) - requested_on.get(name, 0)
            free_slots += max(0, usable // request - on_node)

    return KubernetesView(tuple(pending), free_slots, tuple(burst_nodes))


class KubernetesAdapter:
    # kube-scheduler keeps retrying Pending pods by itself, so the respawn that Swarm needs
    # (docs/burst.md §3.1) has nothing to do here; controller.py skips it
    NEEDS_RESPAWN = False

    def __init__(self, core, namespace, worker_label):
        """core: a kubernetes.client.CoreV1Api. worker_label: "app=worker"."""
        self.core = core
        self.namespace = namespace
        self.worker_label = worker_label
        self.label_key, self.label_value = worker_label.split("=", 1)

    def _is_worker(self, pod):
        return (
            pod["metadata"].get("namespace") == self.namespace
            and pod["metadata"].get("labels", {}).get(self.label_key) == self.label_value
        )

    def read(self, now):
        serialize = self.core.api_client.sanitize_for_serialization
        nodes = serialize(self.core.list_node())["items"]
        pods = serialize(self.core.list_pod_for_all_namespaces(
            field_selector="status.phase!=Succeeded,status.phase!=Failed"))["items"]
        return build_view(nodes, pods, self._is_worker, now)

    def drain(self, node_name):
        """kubectl drain for this one workload: cordon, then evict the worker pods. Each pod gets its
        terminationGracePeriodSeconds (30 s) to finish its job, and the Deployment replaces it
        elsewhere. If an eviction fails, the node is uncordoned again, so that the next iteration,
        which sees it as not draining, tries the whole drain again."""
        self.core.patch_node(node_name, {"spec": {"unschedulable": True}})
        try:
            pods = self.core.list_namespaced_pod(
                self.namespace, label_selector=self.worker_label, field_selector=f"spec.nodeName={node_name}")
            for pod in pods.items:
                self.core.create_namespaced_pod_eviction(pod.metadata.name, self.namespace, {
                    "apiVersion": "policy/v1",
                    "kind": "Eviction",
                    "metadata": {"name": pod.metadata.name, "namespace": self.namespace},
                })
        except Exception:
            self.core.patch_node(node_name, {"spec": {"unschedulable": False}})
            raise

    def remove(self, node_name):
        """Deletes the Node object of an instance that is already gone."""
        self.core.delete_node(node_name)

    def respawn(self, count):
        """Not needed on Kubernetes (NEEDS_RESPAWN); kept so both adapters offer the same methods."""
