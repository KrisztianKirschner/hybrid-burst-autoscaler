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

# The power switch between the orchestrators: change it in a PR to switch clusters.
# Only this cluster's VMs run (plus those with cluster = "none"); both sets at once don't fit in RAM.
variable "active_cluster" {
  description = "Cluster whose VMs are powered on: swarm, k8s or none"
  type        = string
  default     = "swarm"

  # A typo would match no cluster and stop every cluster VM
  validation {
    condition     = contains(["swarm", "k8s", "none"], var.active_cluster)
    error_message = "active_cluster must be \"swarm\", \"k8s\" or \"none\"."
  }
}
