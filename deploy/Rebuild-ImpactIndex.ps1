<#
.SYNOPSIS
    Rebuilds the impact-mapping retrieval index.

.DESCRIPTION
    This is the command every non-Ready health message points at, so its name and switches must
    stay in step with ImpactIndexHealthCheck.RebuildCommand.

    The index is a regenerable cache. The learning store (impact-outcomes.db) sits in a SEPARATE
    directory and is never touched here - -Force clears the index only, so accumulated run
    outcomes that train the ranker survive a rebuild.

    Rebuilding requires the engine's ADO credentials to be available to the host process, since
    the corpus is fetched from Azure DevOps.

.PARAMETER IndexRoot
    Directory holding impact-index.db. Default C:\ProgramData\TestAgentSolution\ImpactIndex.

.PARAMETER Force
    Delete the existing index (and its -wal/-shm sidecars) before rebuilding.

.PARAMETER BaseUrl
    Host exposing the rebuild endpoint. Default http://localhost:5200 (the WPF-embedded WebApi).

.PARAMETER TimeoutMinutes
    Give up after this long. Default 120.

.EXAMPLE
    .\Rebuild-ImpactIndex.ps1
.EXAMPLE
    .\Rebuild-ImpactIndex.ps1 -Force -BaseUrl http://jvgr22:5200
#>
[CmdletBinding()]
param(
    [string]$IndexRoot = (Join-Path $env:ProgramData 'TestAgentSolution\ImpactIndex'),
    [switch]$Force,
    [string]$BaseUrl = 'http://localhost:5200',
    [int]$TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host "  [ .. ] $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Write-Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

$indexFile = Join-Path $IndexRoot 'impact-index.db'

Write-Host "Impact index rebuild" -ForegroundColor White
Write-Host "Index root : $IndexRoot"
Write-Host "Index file : $indexFile"
Write-Host "Host       : $BaseUrl"
Write-Host ""

if (-not (Test-Path $IndexRoot)) {
    Write-Step "creating missing index root"
    New-Item -ItemType Directory -Path $IndexRoot -Force | Out-Null
}

if ($Force) {
    # Sidecars must go too: a stale -wal can resurrect pages from the database we just deleted.
    foreach ($f in @($indexFile, "$indexFile-wal", "$indexFile-shm")) {
        if (Test-Path $f) {
            Write-Step "removing $f"
            try { Remove-Item $f -Force }
            catch { Write-Fail "could not delete ${f}: $($_.Exception.Message)"; Write-Fail "stop the controller/WebApi first."; exit 2 }
        }
    }
    Write-Ok "existing index cleared"
}

$before = if (Test-Path $indexFile) { (Get-Item $indexFile).Length } else { 0 }
$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Step "requesting rebuild (this can take a while)"
$result = $null
try {
    $result = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/impact/index/rebuild" `
        -TimeoutSec ($TimeoutMinutes * 60) -UseDefaultCredentials
}
catch {
    $sw.Stop()
    Write-Fail "rebuild request failed: $($_.Exception.Message)"
    Write-Fail "is the host running at $BaseUrl and is ADO configured?"
    exit 1
}
$sw.Stop()

if (-not (Test-Path $indexFile)) {
    Write-Fail "rebuild reported completion but no index exists at $indexFile"
    exit 1
}

$after = (Get-Item $indexFile).Length
$docs  = if ($result -and $result.PSObject.Properties.Name -contains 'documentCount') { $result.documentCount } else { 'unknown' }

Write-Host ""
Write-Ok ("elapsed        : {0:hh\:mm\:ss}" -f $sw.Elapsed)
Write-Ok ("index size     : {0:N1} MB (was {1:N1} MB)" -f ($after / 1MB), ($before / 1MB))
Write-Ok  "documents      : $docs"

Write-Step "verifying health"
try {
    $health = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health/impact-index" -TimeoutSec 60 -UseDefaultCredentials
    $status = if ($health.PSObject.Properties.Name -contains 'status') { $health.status } else { $health }
    Write-Ok "health status  : $status"
}
catch {
    Write-Host "  [warn] health endpoint unavailable: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Rebuild complete." -ForegroundColor Green
exit 0
