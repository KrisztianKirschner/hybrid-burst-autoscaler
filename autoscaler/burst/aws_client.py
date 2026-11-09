"""The EC2 Auto Scaling group that holds the burst nodes. Takes boto3 clients as arguments."""

from decision import Instance


def _is_active(lifecycle_state):
    # Also matches Pending:Wait and Pending:Proceed; Terminating* instances are already going away
    return lifecycle_state == "InService" or lifecycle_state.startswith("Pending")


class BurstGroup:
    def __init__(self, autoscaling, ec2, name):
        self.autoscaling = autoscaling
        self.ec2 = ec2
        self.name = name

    def read(self):
        """Returns (desired capacity, launching and running instances)."""
        response = self.autoscaling.describe_auto_scaling_groups(AutoScalingGroupNames=[self.name])
        if not response["AutoScalingGroups"]:
            raise LookupError(f"Auto Scaling group {self.name} not found")
        group = response["AutoScalingGroups"][0]
        ids = [i["InstanceId"] for i in group["Instances"] if _is_active(i["LifecycleState"])]
        launched = self._launch_times(ids)
        instances = tuple(Instance(id=i, launched_at=launched[i]) for i in ids)
        return group["DesiredCapacity"], instances

    def _launch_times(self, ids):
        # The group's own listing has no launch time. An empty list would mean "every instance".
        if not ids:
            return {}
        response = self.ec2.describe_instances(InstanceIds=ids)
        return {
            instance["InstanceId"]: instance["LaunchTime"].timestamp()
            for reservation in response["Reservations"]
            for instance in reservation["Instances"]
        }

    def set_desired(self, capacity):
        self.autoscaling.set_desired_capacity(
            AutoScalingGroupName=self.name,
            DesiredCapacity=capacity,
            HonorCooldown=False,
        )

    def terminate(self, instance_id):
        self.autoscaling.terminate_instance_in_auto_scaling_group(
            InstanceId=instance_id,
            ShouldDecrementDesiredCapacity=True,  # or the group would launch a replacement
        )
