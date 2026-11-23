# The cache. The service already works without it (it reads from the database), so a single node is a fair choice.
resource "google_redis_instance" "cache" {
  name           = "${var.name}-cache"
  region         = var.region
  tier           = var.redis_tier
  memory_size_gb = var.redis_memory_gb
  redis_version  = "REDIS_7_2"

  authorized_network = google_compute_network.main.id
  connect_mode       = "PRIVATE_SERVICE_ACCESS"

  # Password required. Encryption in transit is OFF here: the instance is reachable only inside the private network. For
  # production, turn it on (transit_encryption_mode = "SERVER_AUTHENTICATION") and give the service the server CA.
  auth_enabled            = true
  transit_encryption_mode = "DISABLED"

  depends_on = [google_service_networking_connection.private_services]
}
