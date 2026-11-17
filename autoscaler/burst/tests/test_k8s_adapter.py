import unittest
from datetime import datetime, timezone
from types import SimpleNamespace

from k8s_adapter import KubernetesAdapter, build_view, millicores

NOW = datetime(2026, 11, 17, 20, 0, 30, tzinfo=timezone.utc).timestamp()
CREATED = "2026-11-17T20:00:00Z"
INSUFFICIENT = "0/5 nodes are available: 1 node(s) had untolerated taint {node-role.kubernetes.io/control-plane: }, 4 Insufficient cpu."


def k8s_node(name, ready=True, cordoned=False, control_plane=False, cpu="2"):
    labels = {"node-role.kubernetes.io/control-plane": "true"} if control_plane else {}
    return {
        "metadata": {"name": name, "creationTimestamp": CREATED, "labels": labels},
        "spec": {"unschedulable": True} if cordoned else {},
        "status": {
            "allocatable": {"cpu": cpu},
            "conditions": [{"type": "Ready", "status": "True" if ready else "False"}],
        },
    }


def pod(node=None, phase="Running", worker=True, cpu="1", deleting=False, scheduled=None):
    metadata = {
        "name": "p",
        "namespace": "hba" if worker else "kube-system",
        "labels": {"app": "worker"} if worker else {"k8s-app": "kube-dns"},
        "creationTimestamp": CREATED,
    }
    if deleting:
        metadata["deletionTimestamp"] = "2026-11-17T20:00:20Z"
    status = {"phase": phase}
    if scheduled:
        status["conditions"] = [{"type": "PodScheduled", "status": "False", **scheduled}]
    spec = {"containers": [{"resources": {"requests": {"cpu": cpu}}}]}
    if node:
        spec["nodeName"] = node
    return {"metadata": metadata, "spec": spec, "status": status}


def is_worker(p):
    return p["metadata"]["namespace"] == "hba" and p["metadata"]["labels"].get("app") == "worker"


UNSCHEDULABLE = {"reason": "Unschedulable", "message": INSUFFICIENT}


class Millicores(unittest.TestCase):
    def test_quantities(self):
        self.assertEqual([millicores(q) for q in ("2", "1", "900m", "0.5", 1)], [2000, 1000, 900, 500, 1000])


class BuildView(unittest.TestCase):
    def view(self, nodes, pods):
        return build_view(nodes, pods, is_worker, NOW)

    def test_pending_for_lack_of_cpu(self):
        pending = self.view([], [pod(phase="Pending", scheduled=UNSCHEDULABLE)]).pending[0]
        self.assertTrue(pending.lacks_resources)
        self.assertAlmostEqual(pending.age, 30.0)

    def test_pending_for_another_reason(self):
        taint_only = {"reason": "Unschedulable", "message": "0/1 nodes are available: 1 node(s) had untolerated taint."}
        self.assertFalse(self.view([], [pod(phase="Pending", scheduled=taint_only)]).pending[0].lacks_resources)

    def test_not_yet_tried_by_the_scheduler(self):
        self.assertFalse(self.view([], [pod(phase="Pending")]).pending[0].lacks_resources)

    def test_deleted_pending_pods_dont_count(self):
        self.assertEqual(self.view([], [pod(phase="Pending", deleting=True, scheduled=UNSCHEDULABLE)]).pending, ())

    def test_other_workloads_are_not_pending_tasks(self):
        self.assertEqual(self.view([], [pod(phase="Pending", worker=False, scheduled=UNSCHEDULABLE)]).pending, ())

    def test_free_slots_count_only_usable_onprem_workers(self):
        nodes = [
            k8s_node("k8s-ctl", control_plane=True),
            k8s_node("k8s-01"),
            k8s_node("k8s-02"),
            k8s_node("k8s-03", ready=False),
            k8s_node("k8s-04", cordoned=True),
            k8s_node("burst-i-abc"),
        ]
        pods = [pod("k8s-01"), pod("k8s-01"), pod("k8s-02")]
        self.assertEqual(self.view(nodes, pods).onprem_free_slots, 1)

    def test_other_pods_requests_use_up_slots(self):
        # CoreDNS on a worker node: 2000m - 100m leaves one 1-CPU slot, not two
        pods = [pod("k8s-01", worker=False, cpu="100m"), pod("k8s-02")]
        self.assertEqual(self.view([k8s_node("k8s-01"), k8s_node("k8s-02")], pods).onprem_free_slots, 1 + 1)

    def test_finished_pods_free_their_slot(self):
        pods = [pod("k8s-01", phase="Succeeded"), pod("k8s-02")]
        self.assertEqual(self.view([k8s_node("k8s-01")], pods).onprem_free_slots, 2)

    def test_burst_node_fields(self):
        nodes = [k8s_node("burst-i-abc", ready=False, cordoned=True)]
        pods = [pod("burst-i-abc", deleting=True), pod("burst-i-abc", phase="Failed")]  # stopping, finished
        burst = self.view(nodes, pods).nodes[0]
        self.assertEqual((burst.id, burst.instance_id), ("burst-i-abc", "i-abc"))
        self.assertFalse(burst.ready)
        self.assertTrue(burst.draining)
        self.assertEqual(burst.tasks, 1)
        self.assertEqual(burst.joined_at, NOW - 30)

    def test_no_worker_pod_means_no_slot_size(self):
        self.assertEqual(self.view([k8s_node("k8s-01")], []).onprem_free_slots, 0)


class FakeCore:
    """CoreV1Api, as far as drain and remove use it."""

    def __init__(self, pods_on_node, failing_eviction=None):
        self.calls = []
        self.pods_on_node = pods_on_node
        self.failing_eviction = failing_eviction

    def patch_node(self, name, body):
        self.calls.append(("patch", name, body["spec"]["unschedulable"]))

    def list_namespaced_pod(self, namespace, label_selector, field_selector):
        self.calls.append(("list", namespace, label_selector, field_selector))
        return SimpleNamespace(items=[SimpleNamespace(metadata=SimpleNamespace(name=n)) for n in self.pods_on_node])

    def create_namespaced_pod_eviction(self, name, namespace, body):
        if name == self.failing_eviction:
            raise RuntimeError("Too many requests")
        self.calls.append(("evict", name, body["kind"]))

    def delete_node(self, name):
        self.calls.append(("delete", name))


class Drain(unittest.TestCase):
    def test_cordons_then_evicts_each_worker_pod(self):
        core = FakeCore(["w1", "w2"])
        KubernetesAdapter(core, "hba", "app=worker").drain("burst-i-abc")
        self.assertEqual(core.calls, [
            ("patch", "burst-i-abc", True),
            ("list", "hba", "app=worker", "spec.nodeName=burst-i-abc"),
            ("evict", "w1", "Eviction"),
            ("evict", "w2", "Eviction"),
        ])

    def test_failed_eviction_uncordons_so_the_drain_is_retried(self):
        core = FakeCore(["w1", "w2"], failing_eviction="w2")
        with self.assertRaises(RuntimeError):
            KubernetesAdapter(core, "hba", "app=worker").drain("burst-i-abc")
        self.assertEqual(core.calls[-1], ("patch", "burst-i-abc", False))

    def test_remove_deletes_the_node_object(self):
        core = FakeCore([])
        KubernetesAdapter(core, "hba", "app=worker").remove("burst-i-abc")
        self.assertEqual(core.calls, [("delete", "burst-i-abc")])


class Respawn(unittest.TestCase):
    def test_not_needed_on_kubernetes(self):
        self.assertFalse(KubernetesAdapter.NEEDS_RESPAWN)
