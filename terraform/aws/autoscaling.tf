resource "aws_autoscaling_group" "burst_swarm" {
  name                = "hba-burst-swarm"
  min_size            = 0
  max_size            = 2
  desired_capacity    = 0
  vpc_zone_identifier = aws_subnet.burst[*].id
  launch_template {
    id      = aws_launch_template.burst_swarm.id
    version = "$Latest"
  }
  suspended_processes       = ["AZRebalance"]
  wait_for_capacity_timeout = "0"
  lifecycle {
    ignore_changes = [desired_capacity]
  }
}

# The Kubernetes cluster's burst nodes. Same limits and rules as burst_swarm; the burst
# controller running on the active cluster resizes the matching group. Only one cluster runs
# at a time, so at most one of the two groups has instances
resource "aws_autoscaling_group" "burst_k8s" {
  name                = "hba-burst-k8s"
  min_size            = 0
  max_size            = 2
  desired_capacity    = 0
  vpc_zone_identifier = aws_subnet.burst[*].id
  launch_template {
    id      = aws_launch_template.burst_k8s.id
    version = "$Latest"
  }
  suspended_processes       = ["AZRebalance"]
  wait_for_capacity_timeout = "0"
  lifecycle {
    ignore_changes = [desired_capacity]
  }
}
