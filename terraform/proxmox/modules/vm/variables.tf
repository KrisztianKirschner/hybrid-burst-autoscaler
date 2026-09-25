variable "hostname" {
  type = string
}
variable "vmid" {
  type = number
}
variable "template_vmid" {
  type    = number
  default = 9999
}
variable "cores" {
  description = "Number of CPU cores"
  type        = number
  default     = 2
  nullable    = false
}
variable "ip_address" {
  description = "IP address (CIDR)"
  type        = string
}
variable "gateway" {
  type    = string
  default = "10.0.20.1"
}
variable "vlan_id" {
  type    = number
  default = 20
}
variable "username" {
  type    = string
  default = "proxima"
}
variable "memory" {
  description = "Amount of RAM in MB"
  type        = number
  default     = 4096
  nullable    = false
}
variable "disk_size" {
  description = "Disk size in GB"
  type        = number
  default     = 30
  nullable    = false
}
variable "ssh_public_key" {
  description = "Public SSH key for the user account"
  type        = string
}
