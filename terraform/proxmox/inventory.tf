resource "ansible_host" "vm" {
  for_each = local.vms
  name     = each.key
  variables = {
    ansible_host = split("/", each.value.ip_address)[0]
    ansible_user = "proxima"
    cluster      = each.value.cluster
    node_role    = each.value.node_role
  }
}
