output "vpc_id" {
  value = aws_vpc.burst.id
}

output "subnet_ids" {
  value = aws_subnet.burst[*].id
}

output "burst_security_group_id" {
  value = aws_security_group.burst.id
}
