locals {
  # Must match the RustFS bucket: the worker reads both through one HBA_S3_BUCKET
  workload_bucket = "hybrid-burst-data"
}

resource "aws_s3_bucket" "workload" {
  bucket = local.workload_bucket

  tags = {
    Name = local.workload_bucket
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "workload" {
  bucket = aws_s3_bucket.workload.id

  rule {
    id     = "expire-results"
    status = "Enabled"

    filter {
      prefix = "out/"
    }

    expiration {
      days = 7
    }
  }

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"

    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

resource "aws_s3_bucket_public_access_block" "workload" {
  bucket = aws_s3_bucket.workload.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
