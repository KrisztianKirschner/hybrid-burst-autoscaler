terraform {
  required_version = ">= 1.10.0, < 2.0.0"

  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.70"
    }
    ansible = {
      source  = "ansible/ansible"
      version = "~> 1.5"
    }
  }
}
