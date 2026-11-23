<#
.SYNOPSIS
  Runs a k6 load test (in Docker, nothing to install) against the local cluster.
.EXAMPLE
  ./scripts/load-test.ps1                  # 50 req/s for 5 minutes (RNF-02)
  ./scripts/load-test.ps1 -Scenario stress # ramp up to 150 req/s
#>
[CmdletBinding()]
param([ValidateSet('balance', 'stress')] [string] $Scenario = 'balance')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = @{ balance = 'balance-50rps.js'; stress = 'stress-150rps.js' }[$Scenario]
$results = Join-Path $root 'results'
New-Item -ItemType Directory -Force $results | Out-Null

Write-Host "k6: $script (watch it live in Grafana: http://localhost:3030)" -ForegroundColor Cyan
$ErrorActionPreference = 'Continue'   # k6 writes progress to stderr
docker run --rm -i -v "${root}/tests/load:/scripts:ro" -v "${results}:/results" grafana/k6:latest run `
    --summary-export "/results/$Scenario-summary.json" "/scripts/$script" 2>&1 | ForEach-Object { "$_" } | Out-Host
exit $LASTEXITCODE
