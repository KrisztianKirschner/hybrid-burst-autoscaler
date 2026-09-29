locals {

  vms = {
    "obs-01" = {
      vmid       = 230,
      ip_address = "10.0.20.30/24",
      disk_size  = 50
    },
    "swarm-ctl" = {
      vmid       = 211,
      ip_address = "10.0.20.11/24",
      disk_size  = 30
    }
    "swarm-01" = {
      vmid       = 212,
      ip_address = "10.0.20.12/24",
      disk_size  = 30
    }
  }
}

module "vm" {
  source   = "./modules/vm"
  for_each = local.vms

  hostname       = each.key
  vmid           = each.value.vmid
  ip_address     = each.value.ip_address
  template_vmid  = proxmox_virtual_environment_vm.template.vm_id
  ssh_public_key = var.ssh_public_key

  cores     = try(each.value.cores, null)
  memory    = try(each.value.memory, null)
  disk_size = try(each.value.disk_size, null)
}
