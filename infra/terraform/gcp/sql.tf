# One PostgreSQL instance per service, as the architecture says (ADR-0004): the only link between the two is the event.
locals {
  databases = {
    entries       = { database = "entries", user = "entries" }
    consolidation = { database = "consolidation", user = "consolidation" }
  }
}

resource "random_password" "postgres" {
  length  = 32
  special = false
}

resource "google_sql_database_instance" "service" {
  for_each = local.databases

  name                = "${var.name}-${each.key}"
  region              = var.region
  database_version    = var.sql_database_version
  deletion_protection = var.deletion_protection

  settings {
    tier              = var.sql_tier
    availability_type = var.sql_high_availability
    disk_autoresize   = true

    ip_configuration {
      ipv4_enabled    = false
      private_network = google_compute_network.main.id
      ssl_mode        = "ENCRYPTED_ONLY"
    }

    backup_configuration {
      enabled                        = true
      point_in_time_recovery_enabled = true
    }
  }

  depends_on = [google_service_networking_connection.private_services]
}

resource "google_sql_database" "service" {
  for_each = local.databases

  name     = each.value.database
  instance = google_sql_database_instance.service[each.key].name
}

# The chart takes one password for both databases, so both users share it. It is generated here and stored in Secret
# Manager: nobody types it or sees it in a file.
resource "google_sql_user" "service" {
  for_each = local.databases

  name     = each.value.user
  instance = google_sql_database_instance.service[each.key].name
  password = random_password.postgres.result
}
