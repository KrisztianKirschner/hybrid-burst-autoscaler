locals {
  gateway_secret_parameter = "/hba/gateway/tailscale-oauth-secret"
  lab_swarm_block          = "10.0.20.8/29" # advertised by ts-gateway

  swarm_ports = {
    gossip_tcp = { protocol = "tcp", port = 7946 }
    gossip_udp = { protocol = "udp", port = 7946 }
    vxlan      = { protocol = "udp", port = 4789 }
  }
}

# --- AMI lookup ---

data "aws_ami" "ubuntu" {
  most_recent = true
  owners      = ["099720109477"]

  filter {
    name   = "name"
    values = ["ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-arm64-server-*"]
  }

  filter {
    name   = "virtualization-type"
    values = ["hvm"]
  }
}

# --- Security groups ---

resource "aws_security_group" "gateway" {
  name        = "hba-ts-gateway"
  description = "Tailscale subnet router for the VPC"
  vpc_id      = aws_vpc.burst.id

  tags = {
    Name = "hba-ts-gateway"
  }
}

# Direct WireGuard path: the lab side sits behind double NAT, so this side must be reachable
resource "aws_vpc_security_group_ingress_rule" "gateway_tailscale" {
  security_group_id = aws_security_group.gateway.id
  description       = "Tailscale direct connections"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "udp"
  from_port         = 41641
  to_port           = 41641
}

# Everything the burst nodes send towards the lab
resource "aws_vpc_security_group_ingress_rule" "gateway_from_vpc" {
  security_group_id = aws_security_group.gateway.id
  description       = "Traffic to forward from the VPC"
  cidr_ipv4         = aws_vpc.burst.cidr_block
  ip_protocol       = "-1"
}

# Lab traffic arrives with real source addresses (no SNAT on either router)
resource "aws_vpc_security_group_ingress_rule" "burst_from_lab" {
  for_each = local.swarm_ports

  security_group_id = aws_security_group.burst.id
  description       = "Swarm ${each.key} from the lab Swarm nodes"
  cidr_ipv4         = local.lab_swarm_block
  ip_protocol       = each.value.protocol
  from_port         = each.value.port
  to_port           = each.value.port
}

# Path MTU Discovery: the gateway reports "fragmentation needed" (Tailscale 1280 vs VPC 9001).
# For ICMP, from_port is the type and to_port the code.
resource "aws_vpc_security_group_ingress_rule" "burst_icmp_frag_needed" {
  security_group_id = aws_security_group.burst.id
  description       = "ICMP fragmentation needed from the VPC"
  cidr_ipv4         = aws_vpc.burst.cidr_block
  ip_protocol       = "icmp"
  from_port         = 3 # Destination Unreachable
  to_port           = 4 # Fragmentation Needed
}

resource "aws_vpc_security_group_egress_rule" "gateway_all" {
  security_group_id = aws_security_group.gateway.id
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

# --- IAM, Session Manager ---

resource "aws_iam_role" "gateway" {
  name               = "hba-ts-gateway"
  assume_role_policy = data.aws_iam_policy_document.burst_assume.json # same trust: EC2 may assume
}

resource "aws_iam_instance_profile" "gateway" {
  name = "hba-ts-gateway"
  role = aws_iam_role.gateway.name
}

data "aws_iam_policy_document" "gateway" {
  source_policy_documents = [data.aws_iam_policy_document.session_manager.json]

  statement {
    sid       = "TailscaleSecret"
    actions   = ["ssm:GetParameter"]
    resources = ["arn:aws:ssm:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:parameter/hba/gateway/*"]
  }
}

resource "aws_iam_role_policy" "gateway" {
  name   = "hba-ts-gateway"
  role   = aws_iam_role.gateway.id
  policy = data.aws_iam_policy_document.gateway.json
}

# --- EC2 instance ---

resource "aws_instance" "gateway" {
  ami                    = data.aws_ami.ubuntu.id
  instance_type          = "t4g.nano"
  subnet_id              = aws_subnet.burst[0].id
  vpc_security_group_ids = [aws_security_group.gateway.id]
  iam_instance_profile   = aws_iam_instance_profile.gateway.name
  source_dest_check      = false # forwards packets that are not addressed to it

  metadata_options {
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    volume_type = "gp3"
    encrypted   = true
  }

  user_data = templatefile("${path.module}/templates/gateway-user-data.sh.tftpl", {
    secret_parameter = local.gateway_secret_parameter
    region           = data.aws_region.current.region
    vpc_cidr         = aws_vpc.burst.cidr_block
  })
  user_data_replace_on_change = true

  lifecycle {
    ignore_changes = [ami] # a new Ubuntu image must not replace the gateway
  }

  tags = {
    Name = "hba-aws-gateway"
  }
}
