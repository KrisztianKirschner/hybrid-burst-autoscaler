data "aws_ami" "ubuntu_amd64" {
  most_recent = true
  owners      = ["099720109477"]

  filter {
    name   = "name"
    values = ["ubuntu/images/hvm-ssd-gp3/ubuntu-resolute-26.04-amd64-server-20261016"]
  }

  filter {
    name   = "architecture"
    values = ["x86_64"]
  }

  filter {
    name   = "virtualization-type"
    values = ["hvm"]
  }
}

resource "aws_launch_template" "burst_swarm" {
  name                   = "hba-burst-swarm"
  image_id               = data.aws_ami.ubuntu_amd64.id
  instance_type          = "c6a.large"
  vpc_security_group_ids = [aws_security_group.burst.id]

  # SSM and S3, without keys stored on the node
  iam_instance_profile {
    name = aws_iam_instance_profile.burst_node.name
  }

  # IMDSv2 only; hop limit 2 so the worker container can reach the instance role
  metadata_options {
    http_tokens                 = "required"
    http_put_response_hop_limit = 2
  }

  block_device_mappings {
    device_name = "/dev/sda1"

    ebs {
      volume_size           = 20
      volume_type           = "gp3"
      encrypted             = true
      delete_on_termination = true
    }
  }

  user_data = filebase64("${path.module}/templates/burst-swarm-user-data.sh")

  tag_specifications {
    resource_type = "instance"

    tags = {
      Name       = "hba-burst-swarm-node"
      "hba:role" = "burst"
    }
  }
}
