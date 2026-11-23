# GKE Autopilot: Google manages the nodes, so there is no node pool to size. It also enforces NetworkPolicy (the chart's
# default-deny rules need that) and gives every workload the security defaults the chart already asks for.
resource "google_container_cluster" "main" {
  name     = "${var.name}-gke"
  location = var.region

  enable_autopilot    = true
  deletion_protection = var.deletion_protection

  network    = google_compute_network.main.id
  subnetwork = google_compute_subnetwork.main.id

  ip_allocation_policy {
    cluster_secondary_range_name  = "pods"
    services_secondary_range_name = "services"
  }

  # Private nodes (no public address); the control plane stays reachable so that kubectl and helm work from outside.
  private_cluster_config {
    enable_private_nodes    = true
    enable_private_endpoint = false
  }

  release_channel {
    channel = var.gke_release_channel
  }

  depends_on = [google_project_service.apis, google_compute_router_nat.main]
}
