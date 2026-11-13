variable "project_name" {
  description = "Base name for Azure resources"
  type        = string
  default     = "aretheydeadornot"
}

variable "location" {
  description = "Azure region"
  type        = string
  default     = "eastus2"
}

variable "aks_node_vm_size" {
  description = "VM size for AKS nodes (Standard_B2s is cost-effective for POC/learning)"
  type        = string
  default     = "Standard_B2s"
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
