terraform {
  required_version = ">= 1.6"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = ">= 6.0, < 8.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # For a team, keep the state in a bucket (create it once by hand) instead of on one machine:
  #
  # backend "gcs" {
  #   bucket = "<your-state-bucket>"
  #   prefix = "cashflow"
  # }
}

provider "google" {
  project = var.project_id
  region  = var.region
}
