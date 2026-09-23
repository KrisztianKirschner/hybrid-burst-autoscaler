terraform {
  backend "s3" {
    bucket       = "hybrid-burst-tfstate"
    key          = "proxmox/terraform.tfstate"
    region       = "eu-central-1"
    use_lockfile = true
    encrypt      = true
  }
}
