output "vpc_id" {
  value = aws_vpc.burst.id
}

output "subnet_ids" {
  value = aws_subnet.burst[*].id
}

output "burst_security_group_id" {
  value = aws_security_group.burst.id
}

output "workload_bucket_name" {
  value = aws_s3_bucket.workload.bucket
}

output "workload_bucket_arn" {
  value = aws_s3_bucket.workload.arn
}
