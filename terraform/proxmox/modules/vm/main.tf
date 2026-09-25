resource "proxmox_virtual_environment_vm" "vm" {
  name      = var.hostname
  vm_id     = var.vmid
  node_name = "pve"

  clone {
    vm_id   = var.template_vmid
    retries = 3
  }

  cpu {
    cores = var.cores
    type  = "host"
  }

  memory { dedicated = var.memory }

  disk {
    datastore_id = "local-lvm"
    interface    = "virtio0"
    size         = var.disk_size
    discard      = "on"
    iothread     = true
  }

  network_device {
    bridge  = "vmbr1"
    vlan_id = var.vlan_id
  }

  initialization {
    ip_config {
      ipv4 {
        address = var.ip_address
        gateway = var.gateway
      }
    }

    user_account {
      username = var.username
      keys     = [var.ssh_public_key]
    }
  }
}
