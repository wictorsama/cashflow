<#
.SYNOPSIS
  Builds the zip to hand over: only what is committed in Git (no build output, no node_modules, no local files, no secrets).
.EXAMPLE
  ./scripts/package.ps1                 # writes dist/cashflow-<date>.zip
#>
[CmdletBinding()]
param([string] $OutDir = 'dist')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dirty = git status --porcelain
if ($dirty) { throw "There are uncommitted changes. Commit them first, so the zip matches the repository:`n$dirty" }

$commit = (git rev-parse --short HEAD).Trim()
$name = "cashflow-$(Get-Date -Format 'yyyyMMdd')-$commit"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }

# git archive writes exactly the tracked files, inside a folder named like the zip
git archive --format=zip --prefix="$name/" -o $zip HEAD
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }

$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "Created $zip ($size MB) from commit $commit" -ForegroundColor Green
Write-Host 'Inside the zip, start with COMO-EXECUTAR.md.'
