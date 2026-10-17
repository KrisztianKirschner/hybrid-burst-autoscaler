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

# Scoped on purpose: AWS-managed S3 policies would also reach the tfstate bucket
data "aws_iam_policy_document" "burst_node" {
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
    resources = ["arn:aws:ssm:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:parameter/hba/*"]
  }
}

resource "aws_iam_role_policy" "burst_node" {
  name   = "hba-burst-node"
  role   = aws_iam_role.burst_node.id
  policy = data.aws_iam_policy_document.burst_node.json
}

# Session Manager shell: no SSH key, no open port
resource "aws_iam_role_policy_attachment" "burst_node_ssm" {
  role       = aws_iam_role.burst_node.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}
