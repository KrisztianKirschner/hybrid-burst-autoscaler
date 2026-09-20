provider "proxmox" {
  endpoint  = var.PROXMOX_ENDPOINT
  api_token = var.PROXMOX_API_TOKEN
  insecure  = true
}
