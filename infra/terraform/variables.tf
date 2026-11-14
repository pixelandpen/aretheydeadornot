variable "project_name" {
  description = "Base name for Azure resources"
  type        = string
  default     = "aretheydeadornot"
}

variable "location" {
  description = "Azure region for AKS, ACR, and Redis"
  type        = string
  default     = "eastus2"
}

variable "postgres_location" {
  description = "Azure region for PostgreSQL Flexible Server (centralus supports Burstable SKUs)"
  type        = string
  default     = "centralus"
}

variable "aks_node_vm_size" {
  description = "VM size for AKS nodes"
  type        = string
  default     = "Standard_D2s_v6"
}

variable "aks_node_count" {
  description = "Number of AKS nodes"
  type        = number
  default     = 2
}

variable "postgres_admin_username" {
  description = "PostgreSQL admin username"
  type        = string
  default     = "pgadmin"
}

variable "postgres_admin_password" {
  description = "PostgreSQL admin password"
  type        = string
  sensitive   = true
}
