<#
.SYNOPSIS
  Deletes the local kind cluster and everything in it (data included).
#>
[CmdletBinding()]
param([string] $ClusterName = 'cashflow')

# kind reports progress on stderr, which Windows PowerShell 5.1 would treat as an error.
$ErrorActionPreference = 'Continue'
kind delete cluster --name $ClusterName 2>&1 | ForEach-Object { "$_" } | Out-Host
if ($LASTEXITCODE -ne 0) { throw "kind delete cluster failed ($LASTEXITCODE)" }
