locals {
  # Module defaults: cores 2, memory 4096 MB, disk_size 30 GB
  vms = {
    "ts-gateway" = {
      vmid       = 202,
      ip_address = "10.0.20.2/24",
      cores      = 1
      memory     = 512
      disk_size  = 20
      cluster    = "none"
      node_role  = "router"
    }
    "obs-01" = {
      vmid       = 230,
      ip_address = "10.0.20.30/24",
      disk_size  = 50
      cluster    = "none"
      node_role  = "observability"
    },
    "swarm-ctl" = {
      vmid       = 211,
      ip_address = "10.0.20.11/24",
      disk_size  = 30
      cluster    = "swarm"
      node_role  = "control"
    },
    "swarm-01" = {
      vmid       = 212,
      ip_address = "10.0.20.12/24",
      disk_size  = 30
      cluster    = "swarm"
      node_role  = "worker"
    }
    "swarm-02" = {
      vmid       = 213,
      ip_address = "10.0.20.13/24",
      disk_size  = 30
      cluster    = "swarm"
      node_role  = "worker"
    }
    "swarm-03" = {
      vmid       = 214,
      ip_address = "10.0.20.14/24",
      disk_size  = 30
      cluster    = "swarm"
      node_role  = "worker"
    }
    # Kubernetes (k3s) set: the free addresses of the ts-gateway block 10.0.20.8/29,
    # which Swarm shares (.11-.14). Powered on only when active_cluster = "k8s"
    "k8s-ctl" = {
      vmid       = 221,
      ip_address = "10.0.20.8/24",
      disk_size  = 30
      cluster    = "k8s"
      node_role  = "control"
    }
    "k8s-01" = {
      vmid       = 222,
      ip_address = "10.0.20.9/24",
      disk_size  = 30
      cluster    = "k8s"
      node_role  = "worker"
    }
    "k8s-02" = {
      vmid       = 223,
      ip_address = "10.0.20.10/24",
      disk_size  = 30
      cluster    = "k8s"
      node_role  = "worker"
    }
    "k8s-03" = {
      vmid       = 224,
      ip_address = "10.0.20.15/24",
      disk_size  = 30
      cluster    = "k8s"
      node_role  = "worker"
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

  started = contains(["none", var.active_cluster], each.value.cluster)
}
