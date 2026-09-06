<#
.SYNOPSIS
    Pre-deployment safety check - verifies no active sessions before deploying.

.DESCRIPTION
    Queries the WebApi deployment preflight endpoint to determine if it's
    safe to deploy. Blocks deployment if active sessions are running
    unless -Force is specified.

    Should be called before stopping IIS or copying new binaries.

.PARAMETER BaseUrl
    Base URL of the running WebApi. Default: http://localhost:8080

.PARAMETER Force
    Proceed with deployment even if active sessions are detected.

.PARAMETER EnableMaintenance
    Automatically enable maintenance mode if preflight passes.

.EXAMPLE
    .\Invoke-PreDeployCheck.ps1 -BaseUrl "http://jvgr22:8080"
    .\Invoke-PreDeployCheck.ps1 -BaseUrl "http://jvgr22:8080" -Force
    .\Invoke-PreDeployCheck.ps1 -BaseUrl "http://jvgr22:8080" -EnableMaintenance
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:8080",
    [switch]$Force,
    [switch]$EnableMaintenance
)

$ErrorActionPreference = "Stop"
$baseUrl = $BaseUrl.TrimEnd('/')

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "    Pre-Deployment Safety Check" -ForegroundColor Cyan
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "  Target: $baseUrl"
Write-Host ""

# -- Query preflight status --
try {
    $preflight = Invoke-RestMethod -Uri "$baseUrl/api/deployment/preflight" -TimeoutSec 10 -ErrorAction Stop
}
catch {
    Write-Host "  [FAIL] Cannot reach deployment preflight endpoint." -ForegroundColor Red
    Write-Host "         $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    if ($Force) {
        Write-Host "  -Force specified. Proceeding despite API being unreachable." -ForegroundColor Yellow
        exit 0
    }
    exit 1
}

# -- Display results --
Write-Host "  System Status:" -ForegroundColor DarkGray
Write-Host "    Agents: $($preflight.agents.total) total, $($preflight.agents.offline) offline, $($preflight.agents.locked) locked" -ForegroundColor White
Write-Host "    Active sessions: $($preflight.activeSessionCount)" -ForegroundColor White

if ($preflight.issues -and $preflight.issues.Count -gt 0) {
    Write-Host ""
    Write-Host "  Issues:" -ForegroundColor Red
    foreach ($issue in $preflight.issues) {
        Write-Host "    - $issue" -ForegroundColor Red
    }
}

if ($preflight.warnings -and $preflight.warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "  Warnings:" -ForegroundColor Yellow
    foreach ($w in $preflight.warnings) {
        Write-Host "    - $w" -ForegroundColor Yellow
    }
}

Write-Host ""

if ($preflight.safe) {
    Write-Host "  [PASS] $($preflight.summary)" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] $($preflight.summary)" -ForegroundColor Red

    if (-not $Force) {
        Write-Host ""
        Write-Host "  Deployment blocked. Use -Force to override." -ForegroundColor Red
        Write-Host "  ============================================================" -ForegroundColor Red
        exit 1
    }

    Write-Host "  -Force specified. Proceeding despite active sessions." -ForegroundColor Yellow
}

# -- Enable maintenance mode if requested --
if ($EnableMaintenance) {
    Write-Host ""
    Write-Host "  Enabling maintenance mode..." -ForegroundColor Cyan

    $forceParam = if ($Force) { "&force=true" } else { "" }
    try {
        $maintResult = Invoke-RestMethod -Uri "$baseUrl/api/deployment/maintenance/enable?reason=Deployment%20in%20progress$forceParam" -Method POST -TimeoutSec 10 -ErrorAction Stop
        Write-Host "  [PASS] Maintenance mode enabled: $($maintResult.reason)" -ForegroundColor Green
    }
    catch {
        Write-Host "  [WARN] Failed to enable maintenance mode: $($_.Exception.Message)" -ForegroundColor Yellow
        if (-not $Force) { exit 1 }
    }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host "  Deployment may proceed." -ForegroundColor Green
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host ""
exit 0
