variable "PROXMOX_ENDPOINT" {
  type      = string
  sensitive = true
}

variable "PROXMOX_API_TOKEN" {
  type      = string
  sensitive = true
}

variable "ssh_public_key" {
  type      = string
  sensitive = true
}
