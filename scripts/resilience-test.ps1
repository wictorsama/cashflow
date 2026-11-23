<#
.SYNOPSIS
  Injects failures into the running cluster while k6 keeps sending traffic, and reports what the clients saw.
.EXAMPLE
  ./scripts/resilience-test.ps1 -Scenario pod            # deletes one pod of each service, one at a time
  ./scripts/resilience-test.ps1 -Scenario consolidation  # takes the Consolidation service down for 40 s
  ./scripts/resilience-test.ps1 -Scenario assistant      # takes the Assistant down for 40 s (RNF-14)
  ./scripts/resilience-test.ps1 -Scenario database       # kills the Entries database primary pod
  ./scripts/resilience-test.ps1 -Scenario node           # stops the worker node that runs the Entries database primary
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [ValidateSet('pod', 'consolidation', 'node', 'database', 'assistant')] [string] $Scenario)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'results'
New-Item -ItemType Directory -Force $results | Out-Null
$ns = 'cashflow'
$duration = @{ pod = 110; consolidation = 100; node = 250; database = 90; assistant = 100 }[$Scenario]

function Step($m) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $m" -ForegroundColor Cyan }
function Kube { kubectl @args 2>&1 | ForEach-Object { "$_" } }

$summary = "/results/resilience-$Scenario.json"
Step "Starting k6 ($duration s of traffic: 30 reads/s + 10 writes/s)"
$job = Start-Job -ScriptBlock {
    param($root, $results, $duration, $summary)
    docker run --rm -i -v "${root}/tests/load:/scripts:ro" -v "${results}:/results" -e "DURATION=${duration}s" grafana/k6:latest run `
        --summary-export $summary /scripts/resilience.js 2>&1 | Out-Null
} -ArgumentList $root, $results, $duration, $summary

Start-Sleep -Seconds 20

switch ($Scenario) {
    'pod' {
        foreach ($app in 'gateway', 'entries', 'consolidation') {
            Step "Deleting one $app pod"
            $pod = (Kube get pods -n $ns -l "app.kubernetes.io/name=$app" -o name | Select-Object -First 1)
            Kube delete $pod -n $ns --wait=false | Out-Null
            Start-Sleep -Seconds 25
        }
    }
    'consolidation' {
        Step 'Scaling the Consolidation service to 0 (entries must keep being accepted)'
        Kube scale deploy/consolidation -n $ns --replicas=0 | Out-Null
        Start-Sleep -Seconds 40
        Step 'Bringing the Consolidation service back'
        Kube scale deploy/consolidation -n $ns --replicas=2 | Out-Null
        Start-Sleep -Seconds 20
    }
    'assistant' {
        # RNF-14: with the assistant gone, registering and reading must not be affected (only drafts are lost).
        Step 'Scaling the Assistant to 0 (entries and balances must keep working)'
        Kube scale deploy/assistant -n $ns --replicas=0 | Out-Null
        Start-Sleep -Seconds 40
        Step 'Bringing the Assistant back'
        Kube scale deploy/assistant -n $ns --replicas=2 | Out-Null
        Start-Sleep -Seconds 20
    }
    'database' {
        # Only the Entries database primary dies (no node, no other pod): isolates the failover window of the write path.
        $primary = (Kube get pods -n $ns -l 'cnpg.io/cluster=postgres-entries,cnpg.io/instanceRole=primary' -o name | Select-Object -First 1)
        Step "Killing the Entries database primary ($primary) without grace"
        Kube delete $primary -n $ns --grace-period=0 --force | Out-Null
        Start-Sleep -Seconds 60
    }
    'node' {
        # The worst case: the node that runs the Entries database primary (the write path depends on it).
        $victim = (Kube get pods -n $ns -l 'cnpg.io/cluster=postgres-entries,cnpg.io/instanceRole=primary' -o jsonpath='{.items[0].spec.nodeName}')
        $onIt = (Kube get pods -n $ns -o wide --no-headers | Where-Object { $_ -match "\s$($victim)\s" } | ForEach-Object { ($_ -split '\s+')[0] }) -join ', '
        Step "Stopping $victim, which runs the Entries database primary. Pods on it: $onIt"
        docker stop $victim | Out-Null
        # Kubernetes needs ~40 s to declare a silent node dead, then the database fails over: give it time to finish.
        Start-Sleep -Seconds 130
        Step "Starting $victim again"
        docker start $victim | Out-Null
        Start-Sleep -Seconds 30
    }
}

Step 'Waiting for k6 to finish'
Wait-Job $job | Out-Null
Remove-Job $job

$data = Get-Content (Join-Path $results "resilience-$Scenario.json") -Raw | ConvertFrom-Json
foreach ($kind in 'read-balance', 'write-entry') {
    $failed = $data.metrics."http_req_failed{name:$kind}"
    $duration95 = $data.metrics."http_req_duration{name:$kind}"
    if (-not $failed -or -not $duration95) { throw "k6 summary has no metrics for $kind; the report would be meaningless." }
    $total = $failed.passes + $failed.fails
    Write-Host ("{0,-14} requests={1,5}  failed={2,4} ({3:P2})  p95={4:N0} ms  max={5:N0} ms" -f $kind, $total, $failed.passes, ($failed.passes / [Math]::Max(1, $total)), $duration95.'p(95)', $duration95.max) -ForegroundColor Green
}
Step 'Final pod state'
Kube get pods -n $ns
