<#
.SYNOPSIS
  Creates the local kind cluster, builds the images and installs the whole system with Helm.
.EXAMPLE
  ./scripts/up.ps1
#>
[CmdletBinding()]
param(
    [string] $ClusterName = 'cashflow',
    [string] $Namespace = 'cashflow',
    # Leaves out Jaeger, Prometheus and Grafana: much lighter, used by the CI pipeline.
    [switch] $NoObservability,
    # One PostgreSQL instance per database and one RabbitMQ node: no replication, for machines with little memory (CI).
    [switch] $Lightweight
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Run {
    # Runs a native command and stops the script when it fails (PowerShell does not do that by itself).
    # Tools such as kind and docker write progress to stderr; PowerShell 5.1 would turn that into an error.
    $ErrorActionPreference = 'Continue'
    & $args[0] @($args | Select-Object -Skip 1) 2>&1 | ForEach-Object { "$_" } | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $($args -join ' ')" }
}

Step 'Checking prerequisites'
foreach ($tool in 'docker', 'kind', 'helm', 'kubectl') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' was not found. Install it (for example: winget install Kubernetes.kind Helm.Helm) and open a new terminal."
    }
}
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

Step "Cluster '$ClusterName' (1 control-plane + 3 workers)"
# kind prints "No kind clusters found." on stderr, which PowerShell 5.1 treats as an error; ask Docker instead.
$existing = docker ps -a --filter "name=^$ClusterName-control-plane$" --format '{{.Names}}'
if ($existing) {
    Write-Host 'Already exists, reusing it.'
} else {
    Run kind create cluster --config "$root/deploy/kind/kind-config.yaml" --wait 120s
}
Run kubectl config use-context "kind-$ClusterName"

Step 'Building images'
$tag = 'local-' + (Get-Date -Format 'yyyyMMddHHmmss')   # a new tag per run makes Kubernetes roll the pods
foreach ($service in 'entries', 'consolidation', 'assistant', 'gateway', 'web') {
    $context = if ($service -eq 'web') { "$root/web" } else { $root }   # the web app builds from its own folder
    Run docker build -f "$root/deploy/docker/$service.Dockerfile" -t "cashflow/${service}:$tag" $context
    Run kind load docker-image "cashflow/${service}:$tag" --name $ClusterName
}

Step 'Installing the PostgreSQL operator (CloudNativePG)'
# The databases are CloudNativePG clusters, so the operator and its CRDs must exist before the chart is installed.
Run helm repo add cnpg https://cloudnative-pg.github.io/charts --force-update
Run helm upgrade --install cnpg cnpg/cloudnative-pg --version 0.29.2 `
    --namespace cnpg-system --create-namespace -f "$root/deploy/helm/cnpg-values.yaml" --wait --timeout 5m

Step 'Installing the chart'
$extra = if ($NoObservability) { @('--set', 'observability.enabled=false') } else { @() }
if ($Lightweight) { $extra += @('--set', 'postgres.instances=1', '--set', 'rabbitmq.replicas=1') }
Run helm upgrade --install cashflow "$root/deploy/helm/cashflow" `
    --namespace $Namespace --create-namespace `
    -f "$root/deploy/helm/cashflow/values-local.yaml" `
    --set image.tag=$tag @extra `
    --wait --timeout 10m

# Every run builds new tags; drop the ones from earlier runs so they do not fill the disk.
$stale = docker images --format '{{.Repository}}:{{.Tag}}' | Where-Object { $_ -like 'cashflow/*:local-*' -and $_ -notlike "*:$tag" }
foreach ($image in $stale) { docker rmi $image *> $null }

Step 'Ready'
kubectl get pods -n $Namespace -o wide
Write-Host @'

Web application   http://localhost:3000   sign in as merchant1 / local-dev-only
Gateway (API)      http://localhost:8080   POST/GET /v1/entries, GET /v1/balances/{date}
Keycloak (tokens)  http://localhost:8082   realm "cashflow"
Grafana            http://localhost:3030   dashboard "Cash Flow: SLOs and flow" (user admin, password local-dev-only)
Jaeger (traces)    http://localhost:16686

Every call needs a token. Get one (client credentials, local throwaway secret) and use it:

  $token = (Invoke-RestMethod -Method Post http://localhost:8082/realms/cashflow/protocol/openid-connect/token `
      -Body @{ grant_type='client_credentials'; client_id='merchant-integration'; client_secret='local-dev-merchant-secret' }).access_token
  Invoke-RestMethod -Method Post http://localhost:8080/v1/entries -Headers @{ Authorization="Bearer $token"; 'Idempotency-Key'='demo-1' } `
      -ContentType application/json -Body '{"type":"Credit","amount":100.00,"entryDate":"2026-11-17","description":"first sale"}'
  Invoke-RestMethod http://localhost:8080/v1/balances/2026-11-17 -Headers @{ Authorization="Bearer $token" }

Tear down: ./scripts/down.ps1
'@
