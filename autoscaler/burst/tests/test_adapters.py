import unittest
from datetime import datetime, timezone

from aws_client import BurstGroup
from swarm_adapter import build_view

NOW = datetime(2026, 11, 9, 12, 0, 30, tzinfo=timezone.utc).timestamp()
CREATED = "2026-11-09T12:00:00.123456789Z"
ONE_CPU = 1_000_000_000


def swarm_node(node_id, hostname, role="worker", state="ready", availability="active"):
    return {
        "ID": node_id,
        "CreatedAt": CREATED,
        "Spec": {"Role": role, "Availability": availability},
        "Description": {"Hostname": hostname, "Resources": {"NanoCPUs": 2 * ONE_CPU}},
        "Status": {"State": state},
    }


def task(state, node_id=None, desired="running", err=None):
    status = {"State": state}
    if err:
        status["Err"] = err
    result = {"CreatedAt": CREATED, "DesiredState": desired, "Status": status}
    if node_id:
        result["NodeID"] = node_id
    return result


class BuildView(unittest.TestCase):
    def view(self, nodes, tasks):
        return build_view(nodes, tasks, ONE_CPU, NOW)

    def test_pending_for_lack_of_resources(self):
        err = "no suitable node (scheduling constraints not satisfied on 1 node; insufficient resources on 3 nodes)"
        pending = self.view([], [task("pending", err=err)]).pending[0]
        self.assertTrue(pending.lacks_resources)
        self.assertAlmostEqual(pending.age, 29.88, places=1)

    def test_pending_for_another_reason(self):
        err = "no suitable node (scheduling constraints not satisfied on 4 nodes)"
        self.assertFalse(self.view([], [task("pending", err=err)]).pending[0].lacks_resources)

    def test_old_tasks_are_not_pending(self):
        self.assertEqual(self.view([], [task("pending", desired="shutdown")]).pending, ())

    def test_free_slots_count_only_usable_onprem_workers(self):
        nodes = [
            swarm_node("ctl", "swarm-ctl", role="manager"),
            swarm_node("w1", "swarm-01"),
            swarm_node("w2", "swarm-02"),
            swarm_node("w3", "swarm-03", state="down"),
            swarm_node("b1", "burst-i-abc"),
        ]
        tasks = [task("running", "w1"), task("running", "w1"), task("running", "w2")]
        self.assertEqual(self.view(nodes, tasks).onprem_free_slots, 1)

    def test_burst_node_fields(self):
        nodes = [swarm_node("b1", "burst-i-abc", availability="drain")]
        tasks = [
            task("running", "b1", desired="shutdown"),  # still inside its grace period
            task("shutdown", "b1", desired="shutdown"),
        ]
        burst = self.view(nodes, tasks).nodes[0]
        self.assertEqual((burst.id, burst.instance_id), ("b1", "i-abc"))
        self.assertTrue(burst.ready)
        self.assertTrue(burst.draining)
        self.assertEqual(burst.tasks, 1)


class FakeLaunchTime:
    def timestamp(self):
        return 1234.0


class FakeAutoscaling:
    def __init__(self, instances, found=True):
        self.instances = instances
        self.found = found

    def describe_auto_scaling_groups(self, AutoScalingGroupNames):
        if not self.found:
            return {"AutoScalingGroups": []}
        return {"AutoScalingGroups": [{"DesiredCapacity": 2, "Instances": self.instances}]}


class FakeEc2:
    def __init__(self):
        self.asked = []

    def describe_instances(self, InstanceIds):
        self.asked.append(InstanceIds)
        return {"Reservations": [{"Instances": [
            {"InstanceId": i, "LaunchTime": FakeLaunchTime()} for i in InstanceIds
        ]}]}


class ReadGroup(unittest.TestCase):
    def test_skips_terminating_instances(self):
        autoscaling = FakeAutoscaling([
            {"InstanceId": "i-a", "LifecycleState": "InService"},
            {"InstanceId": "i-b", "LifecycleState": "Pending"},
            {"InstanceId": "i-c", "LifecycleState": "Terminating"},
        ])
        desired, instances = BurstGroup(autoscaling, FakeEc2(), "g").read()
        self.assertEqual(desired, 2)
        self.assertEqual([i.id for i in instances], ["i-a", "i-b"])
        self.assertEqual(instances[0].launched_at, 1234.0)

    def test_no_instances_means_no_ec2_call(self):
        ec2 = FakeEc2()
        BurstGroup(FakeAutoscaling([]), ec2, "g").read()
        self.assertEqual(ec2.asked, [])

    def test_missing_group_is_an_error(self):
        with self.assertRaises(LookupError):
            BurstGroup(FakeAutoscaling([], found=False), FakeEc2(), "g").read()


if __name__ == "__main__":
    unittest.main()
