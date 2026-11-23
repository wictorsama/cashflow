variable "project_id" {
  description = "The Google Cloud project that will hold everything."
  type        = string
}

variable "region" {
  description = "Region of the cluster and the managed services. southamerica-east1 is Sao Paulo, close to the merchants."
  type        = string
  default     = "southamerica-east1"
}

variable "name" {
  description = "Prefix of every resource name."
  type        = string
  default     = "cashflow"
}

variable "deletion_protection" {
  description = "Refuse to destroy the cluster and the databases by accident. Turn off only for a throwaway pilot."
  type        = bool
  default     = true
}

variable "sql_tier" {
  description = "Machine type of each PostgreSQL instance (Cloud SQL)."
  type        = string
  default     = "db-custom-1-3840"
}

variable "sql_database_version" {
  description = "PostgreSQL major version on Cloud SQL. The system runs on 17."
  type        = string
  default     = "POSTGRES_17"
}

variable "sql_high_availability" {
  description = "REGIONAL keeps a standby in another zone with automatic failover (production). ZONAL is cheaper (pilot)."
  type        = string
  default     = "REGIONAL"

  validation {
    condition     = contains(["REGIONAL", "ZONAL"], var.sql_high_availability)
    error_message = "Use REGIONAL or ZONAL."
  }
}

variable "redis_tier" {
  description = "BASIC (one node, pilot) or STANDARD_HA (replica with failover). Redis is only a cache, so BASIC is acceptable."
  type        = string
  default     = "BASIC"

  validation {
    condition     = contains(["BASIC", "STANDARD_HA"], var.redis_tier)
    error_message = "Use BASIC or STANDARD_HA."
  }
}

variable "redis_memory_gb" {
  description = "Memory of the Redis cache in GB."
  type        = number
  default     = 1
}

variable "gke_release_channel" {
  description = "How fast the cluster takes Kubernetes upgrades: RAPID, REGULAR or STABLE."
  type        = string
  default     = "REGULAR"
}

variable "create_artifact_registry" {
  description = "Create a private image registry. Not needed if the images come from the public GitHub registry."
  type        = bool
  default     = true
}

variable "demo_users" {
  description = "Create passwords for the demo merchant and auditor of the realm. Turn off in production."
  type        = bool
  default     = true
}
