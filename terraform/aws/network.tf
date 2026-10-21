data "aws_region" "current" {}

locals {
  azs = ["eu-central-1a", "eu-central-1b", "eu-central-1c"]
}


resource "aws_vpc" "burst" {
  cidr_block           = "10.100.0.0/16"
  enable_dns_hostnames = true

  tags = {
    Name = "hba-burst"
  }
}

resource "aws_subnet" "burst" {
  count = length(local.azs)

  vpc_id                  = aws_vpc.burst.id
  availability_zone       = local.azs[count.index]
  cidr_block              = cidrsubnet(aws_vpc.burst.cidr_block, 8, count.index + 1)
  map_public_ip_on_launch = true

  tags = {
    Name = "hba-burst-${local.azs[count.index]}"
  }
}

resource "aws_internet_gateway" "burst" {
  vpc_id = aws_vpc.burst.id

  tags = {
    Name = "hba-burst"
  }
}

resource "aws_route_table" "burst" {
  vpc_id = aws_vpc.burst.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.burst.id
  }

  # The lab (Swarm nodes, VIP, obs-01) through the Tailscale gateway
  route {
    cidr_block           = "10.0.20.0/24"
    network_interface_id = aws_instance.gateway.primary_network_interface_id
  }

  tags = {
    Name = "hba-burst"
  }
}

resource "aws_route_table_association" "burst" {
  count = length(aws_subnet.burst)

  subnet_id      = aws_subnet.burst[count.index].id
  route_table_id = aws_route_table.burst.id
}

resource "aws_vpc_endpoint" "s3" {
  vpc_id            = aws_vpc.burst.id
  service_name      = "com.amazonaws.${data.aws_region.current.region}.s3"
  vpc_endpoint_type = "Gateway"
  route_table_ids   = [aws_route_table.burst.id]

  tags = {
    Name = "hba-burst-s3"
  }
}

resource "aws_security_group" "burst" {
  name        = "hba-burst-node"
  description = "Burst worker nodes"
  vpc_id      = aws_vpc.burst.id

  tags = {
    Name = "hba-burst-node"
  }
}

resource "aws_vpc_security_group_egress_rule" "burst_all" {
  security_group_id = aws_security_group.burst.id
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}
