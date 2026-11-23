<#
.SYNOPSIS
  Checks that this machine can run the system, and (with -Install) installs what is missing through winget.
.EXAMPLE
  ./scripts/prereqs.ps1             # only checks and tells what is missing
  ./scripts/prereqs.ps1 -Install    # also installs kind, helm and kubectl with winget (Windows)
#>
[CmdletBinding()]
param([switch] $Install)

$ErrorActionPreference = 'Continue'
$problems = 0

function Ok($m) { Write-Host "  ok    $m" -ForegroundColor Green }
function Missing($m) { Write-Host "  FALTA $m" -ForegroundColor Red; $script:problems++ }
function Warn($m) { Write-Host "  aviso $m" -ForegroundColor Yellow }

Write-Host "`nPrograms" -ForegroundColor Cyan
$tools = @(
    @{ Name = 'docker';  Winget = 'Docker.DockerDesktop';  Hint = 'Docker Desktop: https://www.docker.com/products/docker-desktop/' },
    @{ Name = 'kind';    Winget = 'Kubernetes.kind';       Hint = 'https://kind.sigs.k8s.io/docs/user/quick-start/#installation' },
    @{ Name = 'helm';    Winget = 'Helm.Helm';             Hint = 'https://helm.sh/docs/intro/install/' },
    @{ Name = 'kubectl'; Winget = 'Kubernetes.kubectl';    Hint = 'https://kubernetes.io/docs/tasks/tools/' }
)
foreach ($tool in $tools) {
    if (Get-Command $tool.Name -ErrorAction SilentlyContinue) { Ok $tool.Name; continue }
    if ($Install -and $tool.Name -ne 'docker' -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host "        installing $($tool.Name) with winget..." -ForegroundColor DarkGray
        winget install --id $tool.Winget -e --accept-source-agreements --accept-package-agreements --silent | Out-Null
        Warn "$($tool.Name) was installed: open a NEW terminal so that it is found, then run this script again."
        continue
    }
    Missing "$($tool.Name). Install it: $($tool.Hint)"
}

Write-Host "`nDocker" -ForegroundColor Cyan
if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Missing 'Docker is installed but not running. Open Docker Desktop and wait until it says "Engine running".'
    } else {
        Ok 'Docker is running'
        $bytes = [double](docker info --format '{{.MemTotal}}' 2>$null)
        $gb = [math]::Round($bytes / 1GB, 1)
        if ($gb -lt 7.5) { Warn "Docker has only $gb GB of memory. Give it at least 8 GB (Docker Desktop > Settings > Resources), or use ./scripts/up.ps1 -Lightweight." }
        else { Ok "Docker memory: $gb GB" }
        $cpus = [int](docker info --format '{{.NCPU}}' 2>$null)
        if ($cpus -lt 4) { Warn "Docker has $cpus CPU(s). 4 or more make the first start much faster." } else { Ok "Docker CPUs: $cpus" }
    }
}

Write-Host "`nPorts the system publishes on this machine" -ForegroundColor Cyan
foreach ($port in 3000, 3030, 8080, 8082, 16686) {
    $busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($busy -and -not (docker ps --format '{{.Names}}' 2>$null | Select-String 'cashflow-control-plane')) {
        Warn "port $port is already in use by another program; the system needs it."
    } else { Ok "port $port is free" }
}

Write-Host ''
if ($problems -gt 0) { Write-Host "$problems requirement(s) missing. Fix them and run this script again." -ForegroundColor Red; exit 1 }
Write-Host 'Everything needed is in place. Next: ./scripts/up.ps1' -ForegroundColor Green
