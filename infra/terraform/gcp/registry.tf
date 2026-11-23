# A private registry for the images, for when they are not pulled from the public GitHub registry.
resource "google_artifact_registry_repository" "images" {
  count = var.create_artifact_registry ? 1 : 0

  location      = var.region
  repository_id = "${var.name}-images"
  format        = "DOCKER"
  description   = "Cash flow service images"

  depends_on = [google_project_service.apis]
}
