output "cluster_name" {
  description = "Name of the GKE cluster."
  value       = google_container_cluster.main.name
}

output "get_credentials" {
  description = "Run this to point kubectl and helm at the new cluster."
  value       = "gcloud container clusters get-credentials ${google_container_cluster.main.name} --region ${var.region} --project ${var.project_id}"
}

output "helm_values_command" {
  description = "Run this to write the generated, secret Helm values (database addresses, passwords) to a file that is NOT committed."
  value       = "gcloud secrets versions access latest --secret=${google_secret_manager_secret.helm_values.secret_id} --project ${var.project_id} > values-gcp-generated.yaml"
}

output "artifact_registry" {
  description = "Prefix to use for image.registry when the images are pushed to the private registry."
  value       = var.create_artifact_registry ? "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.images[0].repository_id}/" : null
}

output "postgres_private_ips" {
  description = "Private addresses of the two databases (also inside the generated Helm values)."
  value       = { for k, v in google_sql_database_instance.service : k => v.private_ip_address }
}

output "redis_endpoint" {
  description = "Private address of the cache."
  value       = "${google_redis_instance.cache.host}:${google_redis_instance.cache.port}"
}
