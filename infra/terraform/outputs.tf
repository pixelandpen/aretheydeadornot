output "resource_group_name" {
  value = azurerm_resource_group.rg.name
}

output "acr_login_server" {
  value = azurerm_container_registry.acr.login_server
}

output "aks_cluster_name" {
  value = azurerm_kubernetes_cluster.aks.name
}

output "postgres_primary_fqdn" {
  value = azurerm_postgresql_flexible_server.primary.fqdn
}

output "redis_hostname" {
  value = azurerm_managed_redis.redis.hostname
}

output "redis_port" {
  value = azurerm_managed_redis.redis.default_database[0].port
}
