import unittest

from decision import Cluster, Instance, Node, PendingTask, Policy, decide

POLICY = Policy(
    min_nodes=0,
    max_nodes=2,
    new_pod_scale_up_delay=10,
    scale_down_unneeded_time=120,
    max_node_provision_time=480,
    replicas_per_node=2,
)
NOW = 10_000.0
WAITING = PendingTask(age=30, lacks_resources=True)


def node(name, tasks=2, ready=True, draining=False):
    return Node(id=f"n-{name}", instance_id=f"i-{name}", ready=ready, draining=draining, tasks=tasks)


def instance(name, age=100):
    return Instance(id=f"i-{name}", launched_at=NOW - age)


def cluster(pending=(), free=0, nodes=(), desired=0, instances=()):
    return Cluster(tuple(pending), free, tuple(nodes), desired, tuple(instances))


def run(state, unneeded_since=None):
    return decide(state, POLICY, unneeded_since or {}, NOW)


class ScaleUp(unittest.TestCase):
    def test_four_pending_asks_for_two_nodes(self):
        self.assertEqual(run(cluster(pending=[WAITING] * 4)).set_desired, 2)

    def test_one_pending_rounds_up_to_one_node(self):
        self.assertEqual(run(cluster(pending=[WAITING])).set_desired, 1)

    def test_capped_at_max_nodes(self):
        self.assertEqual(run(cluster(pending=[WAITING] * 9)).set_desired, 2)

    def test_young_task_does_not_count(self):
        young = PendingTask(age=5, lacks_resources=True)
        self.assertIsNone(run(cluster(pending=[young] * 4)).set_desired)

    def test_other_pending_reason_does_not_count(self):
        typo = PendingTask(age=30, lacks_resources=False)
        self.assertIsNone(run(cluster(pending=[typo] * 4)).set_desired)

    def test_booting_nodes_are_not_launched_twice(self):
        state = cluster(pending=[WAITING] * 4, desired=2, instances=[instance("a"), instance("b")])
        self.assertIsNone(run(state).set_desired)

    def test_tasks_on_burst_nodes_add_to_the_need(self):
        state = cluster(pending=[WAITING] * 2, nodes=[node("a")], desired=1, instances=[instance("a")])
        self.assertEqual(run(state).set_desired, 2)

    def test_never_lowers_desired(self):
        state = cluster(nodes=[node("a", tasks=0)], desired=2, instances=[instance("a"), instance("b")])
        self.assertIsNone(run(state).set_desired)


class ScaleDown(unittest.TestCase):
    def test_timer_starts_when_tasks_fit(self):
        state = cluster(free=2, nodes=[node("a")], desired=1, instances=[instance("a")])
        result = run(state)
        self.assertEqual(result.unneeded_since, {"n-a": NOW})
        self.assertIsNone(result.drain)

    def test_drains_after_unneeded_time(self):
        state = cluster(free=2, nodes=[node("a")], desired=1, instances=[instance("a")])
        self.assertEqual(run(state, {"n-a": NOW - 120}).drain, "n-a")

    def test_timer_resets_when_tasks_no_longer_fit(self):
        state = cluster(free=1, nodes=[node("a")], desired=1, instances=[instance("a")])
        self.assertEqual(run(state, {"n-a": NOW - 500}).unneeded_since, {})

    def test_any_pending_task_blocks_scale_down(self):
        young = PendingTask(age=1, lacks_resources=True)
        state = cluster(pending=[young], free=6, nodes=[node("a")], desired=1, instances=[instance("a")])
        result = run(state, {"n-a": NOW - 500})
        self.assertIsNone(result.drain)
        self.assertEqual(result.unneeded_since, {})

    def test_one_node_at_a_time(self):
        nodes = [node("a", tasks=0, draining=True), node("b", tasks=0)]
        state = cluster(free=6, nodes=nodes, desired=2, instances=[instance("a"), instance("b")])
        self.assertIsNone(run(state, {"n-b": NOW - 500}).drain)

    def test_drains_the_emptiest_node_first(self):
        nodes = [node("a", tasks=2), node("b", tasks=1)]
        state = cluster(free=6, nodes=nodes, desired=2, instances=[instance("a"), instance("b")])
        self.assertEqual(run(state, {"n-a": NOW - 500, "n-b": NOW - 500}).drain, "n-b")

    def test_tasks_can_move_to_another_burst_node(self):
        nodes = [node("a", tasks=1), node("b", tasks=1)]
        state = cluster(free=0, nodes=nodes, desired=2, instances=[instance("a"), instance("b")])
        self.assertEqual(set(run(state).unneeded_since), {"n-a", "n-b"})

    def test_respects_min_nodes(self):
        policy = Policy(1, 2, 10, 120, 480, 2)
        state = cluster(free=6, nodes=[node("a", tasks=0)], desired=1, instances=[instance("a")])
        self.assertIsNone(decide(state, policy, {"n-a": NOW - 500}, NOW).drain)

    def test_waits_for_tasks_to_stop_before_terminating(self):
        state = cluster(free=6, nodes=[node("a", tasks=2, draining=True)], desired=1, instances=[instance("a")])
        self.assertIsNone(run(state).terminate)

    def test_terminates_once_drained(self):
        state = cluster(free=6, nodes=[node("a", tasks=0, draining=True)], desired=1, instances=[instance("a")])
        self.assertEqual(run(state).terminate, {"i-a": "drained"})

    def test_removes_node_once_instance_is_gone_and_down(self):
        state = cluster(free=6, nodes=[node("a", tasks=0, ready=False, draining=True)])
        result = run(state)
        self.assertEqual(result.remove, ("n-a",))
        self.assertIsNone(result.terminate)

    def test_keeps_node_until_swarm_reports_it_down(self):
        state = cluster(free=6, nodes=[node("a", tasks=0, draining=True)])
        self.assertEqual(run(state).remove, ())


class Failures(unittest.TestCase):
    def test_terminates_instance_that_never_joined(self):
        state = cluster(desired=1, instances=[instance("a", age=480)])
        self.assertEqual(run(state).terminate, {"i-a": "never joined"})

    def test_booting_instance_is_left_alone(self):
        state = cluster(desired=1, instances=[instance("a", age=479)])
        self.assertIsNone(run(state).terminate)

    def test_short_outage_keeps_the_node(self):
        state = cluster(nodes=[node("a", ready=False)], desired=1, instances=[instance("a", age=9000)])
        result = run(state)
        self.assertIsNone(result.terminate)
        self.assertEqual(result.unneeded_since, {"n-a": NOW})

    def test_terminates_node_down_for_the_unneeded_time(self):
        state = cluster(nodes=[node("a", ready=False)], desired=1, instances=[instance("a", age=9000)])
        self.assertEqual(run(state, {"n-a": NOW - 120}).terminate, {"i-a": "not ready"})

    def test_terminates_draining_node_that_went_down(self):
        state = cluster(nodes=[node("a", tasks=2, ready=False, draining=True)], desired=1, instances=[instance("a")])
        self.assertEqual(run(state).terminate, {"i-a": "drained"})


if __name__ == "__main__":
    unittest.main()
