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
    Host exposing the rebuild endpoint. This is the WebAPI (the IIS site, default port 81) - it owns index
    maintenance. The WPF controller on 5200 does NOT map /api/impact-mapping.

.PARAMETER TimeoutMinutes
    Give up after this long. Default 120.

.EXAMPLE
    .\Rebuild-ImpactIndex.ps1
.EXAMPLE
    .\Rebuild-ImpactIndex.ps1 -Force -BaseUrl http://jvgr22:81
#>
[CmdletBinding()]
param(
    [string]$IndexRoot = (Join-Path $env:ProgramData 'TestAgentSolution\ImpactIndex'),
    [switch]$Force,
    [string]$BaseUrl = 'http://localhost:81',
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

# The rebuild endpoint is fire-and-forget: it returns 202 immediately and builds on a background task,
# so completion has to be observed by polling the status endpoint, not by waiting on the POST.
$rebuildUri = "$BaseUrl/api/impact-mapping/index/rebuild"
$statusUri  = "$BaseUrl/api/impact-mapping/index/status"

function Get-IndexStatus {
    try { return Invoke-RestMethod -Method Get -Uri $statusUri -TimeoutSec 60 -UseDefaultCredentials }
    catch { return $null }
}

$startStatus = Get-IndexStatus
$startBuiltUtc = if ($startStatus) { "$($startStatus.builtUtc)" } else { "" }

# The rebuild is fire-and-forget, so a credential failure looks identical to a slow build: the POST still
# returns 202 and BuiltUtc simply never advances. Probe the host's live ADO credential first so an expired
# PAT fails here in seconds instead of after a silent 120-minute poll.
Write-Step "checking the host's ADO credential"
try {
    $adoHealth = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/impact/health" -TimeoutSec 60 -UseDefaultCredentials
    if ($adoHealth -and -not $adoHealth.adoReachable) {
        Write-Fail "the host cannot authenticate to Azure DevOps, so there is no corpus to index."
        if ($adoHealth.probeError) { Write-Fail "ADO said: $($adoHealth.probeError)" }
        Write-Fail "the PAT is read from the ADO_PAT environment variable on the HOST."
        Write-Fail "for the IIS-hosted WebAPI it must be Machine scope, followed by iisreset:"
        Write-Fail "  [Environment]::SetEnvironmentVariable('ADO_PAT','<new-pat>','Machine'); iisreset"
        exit 3
    }
    Write-Ok "ADO reachable (components loaded: $($adoHealth.componentCount))"
}
catch {
    # Never block the rebuild on a probe that is itself unavailable (older host, auth quirk).
    Write-Host "  [warn] could not read $BaseUrl/api/impact/health: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Step "requesting rebuild (Admin rights required)"
try {
    Invoke-RestMethod -Method Post -Uri $rebuildUri -TimeoutSec 120 -UseDefaultCredentials | Out-Null
}
catch {
    $sw.Stop()
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    Write-Fail "rebuild request failed (HTTP $code): $($_.Exception.Message)"
    switch ($code) {
        401 { Write-Fail "not authenticated - run as a user the controller recognises." }
        403 { Write-Fail "authenticated but not an Admin; /index/rebuild requires the Admin policy." }
        404 { Write-Fail "endpoint not found - /api/impact-mapping lives on the WebAPI (IIS site, default port 81), not the WPF controller on 5200." }
        default { Write-Fail "is the host running at $BaseUrl and is ADO configured?" }
    }
    exit 1
}
Write-Ok "rebuild accepted (202); waiting for completion"

# Done when BuiltUtc advances past the value captured before the POST.
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$done = $false
$status = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    $status = Get-IndexStatus
    if ($null -eq $status) { continue }
    if ("$($status.builtUtc)" -ne $startBuiltUtc -and -not [string]::IsNullOrWhiteSpace("$($status.builtUtc)")) {
        $done = $true
        break
    }
    Write-Host ("      still building... {0:mm\:ss} elapsed" -f $sw.Elapsed) -ForegroundColor DarkGray
}
$sw.Stop()

if (-not $done) {
    Write-Fail "rebuild did not report completion within $TimeoutMinutes minute(s)."
    Write-Fail "the build runs in the background, so check the WebAPI stdout log on the HOST:"
    Write-Fail "  C:\inetpub\TestControllerWeb\logs\stdout_*.log  (look for RetrievalIndexBuilder / AdoApiException)"
    Write-Fail "if the index file never grew, the build failed early - an ADO 401 is the usual cause."
    exit 1
}

if (-not (Test-Path $indexFile)) {
    Write-Fail "rebuild reported completion but no index exists at $indexFile"
    Write-Fail "the host may be resolving a different IMPACT_INDEX_ROOT - check its startup log."
    exit 1
}

$after = (Get-Item $indexFile).Length
$docs  = if ($status -and $status.PSObject.Properties.Name -contains 'documentCount') { $status.documentCount } else { 'unknown' }

Write-Host ""
Write-Ok ("elapsed        : {0:hh\:mm\:ss}" -f $sw.Elapsed)
Write-Ok ("index size     : {0:N1} MB (was {1:N1} MB)" -f ($after / 1MB), ($before / 1MB))
Write-Ok  "documents      : $docs"
Write-Ok  "built (UTC)    : $($status.builtUtc)"

Write-Step "verifying health"
try {
    $health = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health/impact-index" -TimeoutSec 120 -UseDefaultCredentials
    $status2 = if ($health.PSObject.Properties.Name -contains 'status') { $health.status } else { $health }
    Write-Ok "health status  : $status2"
}
catch {
    Write-Host "  [warn] health endpoint unavailable: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Rebuild complete." -ForegroundColor Green
exit 0
