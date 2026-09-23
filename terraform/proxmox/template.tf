resource "proxmox_download_file" "cloud_image" {
  content_type = "import"
  datastore_id = "local"
  node_name    = "pve"
  url          = "https://cloud-images.ubuntu.com/resolute/current/resolute-server-cloudimg-amd64.img"
  file_name    = "resolute-server-cloudimg-amd64.qcow2"
}

resource "proxmox_virtual_environment_vm" "template" {
  name      = "Ubuntu-server-2604-template"
  vm_id     = 9999
  node_name = "pve"
  template  = true
  started   = false
  machine   = "q35"

  cpu {
    cores = 4
    type  = "x86-64-v2-AES"
  }

  memory { dedicated = 4096 }

  disk {
    datastore_id = "local-lvm"
    import_from  = proxmox_download_file.cloud_image.id
    interface    = "virtio0"
    size         = 20
  }

  network_device { bridge = "vmbr1" }

  initialization {
    ip_config {
      ipv4 {
        address = "dhcp"
      }
    }
  }
}
