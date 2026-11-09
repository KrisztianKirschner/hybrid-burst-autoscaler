data "aws_caller_identity" "current" {}

# Who may assume the role: EC2 instances
data "aws_iam_policy_document" "burst_assume" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "burst_node" {
  name               = "hba-burst-node"
  assume_role_policy = data.aws_iam_policy_document.burst_assume.json
}

resource "aws_iam_instance_profile" "burst_node" {
  name = "hba-burst-node"
  role = aws_iam_role.burst_node.name
}

# https://docs.aws.amazon.com/systems-manager/latest/userguide/getting-started-create-iam-instance-profile.html
data "aws_iam_policy_document" "session_manager" {
  statement {
    sid = "SessionManager"
    actions = [
      "ssm:UpdateInstanceInformation",    # register
      "ssmmessages:CreateControlChannel", # control channel
      "ssmmessages:OpenControlChannel",
      "ssmmessages:CreateDataChannel", # data channel for agent
      "ssmmessages:OpenDataChannel"
    ]
    resources = ["*"]
  }
}

# Scoped on purpose: AWS-managed S3 policies would also reach the tfstate bucket
data "aws_iam_policy_document" "burst_node" {
  source_policy_documents = [data.aws_iam_policy_document.session_manager.json]

  statement {
    sid       = "WorkloadObjects"
    actions   = ["s3:GetObject", "s3:PutObject"]
    resources = ["${aws_s3_bucket.workload.arn}/*"]
  }

  statement {
    sid       = "WorkloadList"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.workload.arn]
  }

  statement {
    sid       = "JoinParameters"
    actions   = ["ssm:GetParameter"]
    resources = ["arn:aws:ssm:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:parameter/hba/burst/*"]
  }
}

resource "aws_iam_role_policy" "burst_node" {
  name   = "hba-burst-node"
  role   = aws_iam_role.burst_node.id
  policy = data.aws_iam_policy_document.burst_node.json
}

# The burst controller runs on swarm-ctl, on-prem, so it can't use an instance role.5555
resource "aws_iam_user" "burst_controller" {
  name = "hba-burst-controller"
}

data "aws_iam_policy_document" "burst_controller" {
  statement {
    sid = "ResizeBurstGroup"
    actions = [
      "autoscaling:SetDesiredCapacity",
      "autoscaling:TerminateInstanceInAutoScalingGroup"
    ]
    resources = [aws_autoscaling_group.burst_swarm.arn]
  }

  # AWS doesn't allow limiting these Describe calls to one resource
  statement {
    sid = "ReadBurstState"
    actions = [
      "autoscaling:DescribeAutoScalingGroups",
      "ec2:DescribeInstances"
    ]
    resources = ["*"]
  }
}

resource "aws_iam_user_policy" "burst_controller" {
  name   = "hba-burst-controller"
  user   = aws_iam_user.burst_controller.name
  policy = data.aws_iam_policy_document.burst_controller.json
}
