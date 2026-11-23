# Every secret the chart asks for is generated here and kept in Secret Manager, as one file of Helm values. Nobody invents
# or pastes a password, and the repository never holds one.
resource "random_password" "generated" {
  for_each = toset([
    "rabbitmq",
    "erlang_cookie",
    "keycloak_admin",
    "merchant_integration",
    "ops_admin",
    "reconciler",
    "auditor",
    "grafana_admin",
    "web_auth",
    "merchant1",
    "auditor1",
  ])

  length  = 32
  special = false
}

locals {
  generated = { for k, v in random_password.generated : k => v.result }

  helm_values = {
    postgres = {
      password = random_password.postgres.result
    }
    rabbitmq = {
      password     = local.generated["rabbitmq"]
      erlangCookie = local.generated["erlang_cookie"]
    }
    keycloak = {
      adminPassword             = local.generated["keycloak_admin"]
      merchantIntegrationSecret = local.generated["merchant_integration"]
      opsAdminSecret            = local.generated["ops_admin"]
      reconcilerSecret          = local.generated["reconciler"]
      auditorSecret             = local.generated["auditor"]
      auditor1Password          = local.generated["auditor1"]
      merchant1Password         = local.generated["merchant1"]
    }
    observability = {
      grafana = { adminPassword = local.generated["grafana_admin"] }
    }
    web = {
      authSecret = local.generated["web_auth"]
    }
    external = {
      postgres = {
        enabled = true
        entries = {
          host     = google_sql_database_instance.service["entries"].private_ip_address
          database = local.databases["entries"].database
          username = local.databases["entries"].user
        }
        consolidation = {
          host     = google_sql_database_instance.service["consolidation"].private_ip_address
          database = local.databases["consolidation"].database
          username = local.databases["consolidation"].user
        }
      }
      redis = {
        enabled          = true
        connectionString = "${google_redis_instance.cache.host}:${google_redis_instance.cache.port},password=${google_redis_instance.cache.auth_string}"
      }
    }
  }
}

resource "google_secret_manager_secret" "helm_values" {
  secret_id = "${var.name}-helm-values"

  replication {
    auto {}
  }

  depends_on = [google_project_service.apis]
}

resource "google_secret_manager_secret_version" "helm_values" {
  secret      = google_secret_manager_secret.helm_values.id
  secret_data = yamlencode(local.helm_values)
}
