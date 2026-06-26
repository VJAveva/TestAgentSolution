<#
.SYNOPSIS
    Deploy-or-rollback orchestrator for TestAgentSolution components.

.DESCRIPTION
    Wraps the existing per-component deploy scripts (deploy-webapi.bat,
    deploy-controller.bat, deploy-agent.bat) with the safety net they lack:

      1. PRE-DEPLOY  — runs Invoke-PreDeployCheck.ps1 (blocks on active sessions).
      2. BACKUP      — snapshots the current remote deployment to a timestamped
                       folder BEFORE the destructive `robocopy /MIR` overwrites it.
      3. DEPLOY      — invokes the matching component deploy script.
      4. VERIFY      — runs Invoke-SmokeTest.ps1 against the deployed host.
      5. ROLLBACK    — if VERIFY fails, automatically restores the backup.

    Re-run with -Rollback to manually restore the most recent (or a named)
    backup without deploying anything new.

    This is the automation referenced by .github/workflows/deploy.yml and the
    "Deploy & Rollback" section of docs/RUNBOOK.md. Anyone — not just the
    original author — can deploy and roll back with a single command.

.PARAMETER Component
    Which component to act on: webapi | controller | agent.

.PARAMETER TargetNode
    Machine name or IP of the target node.

.PARAMETER BaseUrl
    Base URL of the running WebApi used for pre-deploy + smoke-test checks.
    Required for the 'webapi' component; optional for controller/agent.

.PARAMETER IisSiteName
    IIS site name for the 'webapi' component. Default: TestControllerWeb.

.PARAMETER ControllerNode
    Controller machine name — required when Component is 'agent'.

.PARAMETER Rollback
    Restore a backup instead of deploying. Uses -BackupName if given,
    otherwise the most recent backup for the component+node.

.PARAMETER BackupName
    Specific backup folder name (timestamp) to restore. Implies -Rollback.

.PARAMETER BackupRoot
    Root folder on this machine where backups are kept.
    Default: <repo>\publish\_backups.

.PARAMETER Force
    Pass through to the pre-deploy check (proceed despite active sessions) and
    skip interactive rollback confirmation.

.PARAMETER SkipSmokeTest
    Deploy without running the post-deploy smoke test (NOT recommended).

.EXAMPLE
    # Deploy the WebApi with full safety net + auto-rollback on smoke failure
    .\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -BaseUrl http://WEBSERVER01

.EXAMPLE
    # Roll the WebApi back to the previous deployment
    .\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -BaseUrl http://WEBSERVER01 -Rollback

.EXAMPLE
    # Restore a specific snapshot
    .\Invoke-Deploy.ps1 -Component webapi -TargetNode WEBSERVER01 -Rollback -BackupName 20260626-141200
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('webapi', 'controller', 'agent')]
    [string]$Component,

    [Parameter(Mandatory = $true)]
    [string]$TargetNode,

    [string]$BaseUrl,

    [string]$IisSiteName = 'TestControllerWeb',

    [string]$ControllerNode,

    [switch]$Rollback,

    [string]$BackupName,

    [string]$BackupRoot,

    [switch]$Force,

    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir

if ($BackupName) { $Rollback = $true }
if (-not $BackupRoot) { $BackupRoot = Join-Path $repoRoot 'publish\_backups' }

# ── Resolve the remote deployment path for the component ──────────────────────
function Get-RemotePath {
    switch ($Component) {
        'webapi'     { "\\$TargetNode\C`$\inetpub\$IisSiteName" }
        'controller' { "\\$TargetNode\C`$\TestControllerService" }
        'agent'      { "\\$TargetNode\C`$\TestAgentService" }
    }
}

$remotePath = Get-RemotePath
$componentBackupDir = Join-Path $BackupRoot "$Component-$TargetNode"

function Write-Step($msg) { Write-Host "  [deploy] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [ ok   ] $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "  [ warn ] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "  [ FAIL ] $msg" -ForegroundColor Red }

# ── IIS site control (webapi only) ────────────────────────────────────────────
function Set-WebApiSite([ValidateSet('Start', 'Stop')] [string]$Action) {
    if ($Component -ne 'webapi') { return }
    $local = $env:COMPUTERNAME
    $cmd = if ($Action -eq 'Stop') { 'Stop-WebSite' } else { 'Start-WebSite' }
    $sb = "Import-Module WebAdministration -ErrorAction SilentlyContinue; $cmd -Name '$IisSiteName' -ErrorAction SilentlyContinue"
    if ($TargetNode -ieq $local) {
        powershell -NoProfile -Command $sb 2>$null
    }
    else {
        Invoke-Command -ComputerName $TargetNode -ScriptBlock ([scriptblock]::Create($sb)) 2>$null
    }
}

# ── Snapshot the current remote deployment before overwriting it ──────────────
function Backup-CurrentDeployment {
    if (-not (Test-Path $remotePath)) {
        Write-Warn2 "No existing deployment at $remotePath — nothing to back up (first deploy)."
        return $null
    }
    if (-not (Get-ChildItem -Path $remotePath -Force -ErrorAction SilentlyContinue)) {
        Write-Warn2 "Existing deployment at $remotePath is empty — skipping backup."
        return $null
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dest = Join-Path $componentBackupDir $stamp
    Write-Step "Backing up $remotePath -> $dest"
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    & robocopy $remotePath $dest /MIR /NJH /NJS /NDL /NP /NFL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Backup robocopy failed with exit code $LASTEXITCODE." }
    Write-Ok "Backup created: $stamp"
    return $dest
}

# ── Restore a backup over the remote deployment ───────────────────────────────
function Restore-Backup([string]$BackupPath) {
    if (-not (Test-Path $BackupPath)) { throw "Backup path not found: $BackupPath" }
    Write-Step "Restoring $BackupPath -> $remotePath"
    Set-WebApiSite -Action Stop
    if (-not (Test-Path $remotePath)) { New-Item -ItemType Directory -Path $remotePath -Force | Out-Null }
    & robocopy $BackupPath $remotePath /MIR /NJH /NJS /NDL /NP /NFL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Restore robocopy failed with exit code $LASTEXITCODE." }
    Set-WebApiSite -Action Start
    Write-Ok "Restore complete from $(Split-Path -Leaf $BackupPath)"
}

function Get-LatestBackup {
    if (-not (Test-Path $componentBackupDir)) { return $null }
    Get-ChildItem -Path $componentBackupDir -Directory |
        Sort-Object Name -Descending | Select-Object -First 1
}

# ── Invoke the existing component deploy script ───────────────────────────────
function Invoke-ComponentDeploy {
    switch ($Component) {
        'webapi' {
            Write-Step "Running deploy-webapi.bat $TargetNode $IisSiteName"
            & (Join-Path $scriptDir 'deploy-webapi.bat') $TargetNode $IisSiteName
        }
        'controller' {
            Write-Step "Running deploy-controller.bat $TargetNode"
            & (Join-Path $scriptDir 'deploy-controller.bat') $TargetNode
        }
        'agent' {
            if (-not $ControllerNode) { throw "Component 'agent' requires -ControllerNode." }
            Write-Step "Running deploy-agent.bat $TargetNode $ControllerNode"
            & (Join-Path $scriptDir 'deploy-agent.bat') $TargetNode $ControllerNode
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "Component deploy script failed with exit code $LASTEXITCODE." }
}

# ── Pre-deploy + smoke checks (webapi has the HTTP surface to test) ───────────
function Invoke-PreDeploy {
    if ($Component -ne 'webapi' -or -not $BaseUrl) { return }
    Write-Step "Pre-deploy safety check against $BaseUrl"
    $args = @{ BaseUrl = $BaseUrl }
    if ($Force) { $args.Force = $true }
    & (Join-Path $scriptDir 'Invoke-PreDeployCheck.ps1') @args
    if ($LASTEXITCODE -ne 0) { throw "Pre-deploy check blocked the deployment (active sessions). Use -Force to override." }
}

function Test-Deployment {
    if ($SkipSmokeTest -or $Component -ne 'webapi' -or -not $BaseUrl) { return $true }
    Write-Step "Smoke-testing $BaseUrl"
    try {
        & (Join-Path $scriptDir 'Invoke-SmokeTest.ps1') -BaseUrl $BaseUrl
        return ($LASTEXITCODE -eq 0)
    }
    catch {
        Write-Err "Smoke test threw: $($_.Exception.Message)"
        return $false
    }
}

Write-Host ''
Write-Host '  ============================================================' -ForegroundColor White
Write-Host "    TestAgentSolution Deploy Orchestrator" -ForegroundColor White
Write-Host "    Component : $Component" -ForegroundColor White
Write-Host "    Target    : $TargetNode  ($remotePath)" -ForegroundColor White
Write-Host "    Mode      : $(if ($Rollback) { 'ROLLBACK' } else { 'DEPLOY' })" -ForegroundColor White
Write-Host '  ============================================================' -ForegroundColor White
Write-Host ''

# ── ROLLBACK path ─────────────────────────────────────────────────────────────
if ($Rollback) {
    $backup = if ($BackupName) {
        Join-Path $componentBackupDir $BackupName
    }
    else {
        $latest = Get-LatestBackup
        if (-not $latest) { Write-Err "No backups found in $componentBackupDir."; exit 1 }
        $latest.FullName
    }
    if (-not $Force -and -not $PSCmdlet.ShouldProcess($remotePath, "Restore backup '$(Split-Path -Leaf $backup)'")) {
        Write-Warn2 'Rollback cancelled.'
        exit 0
    }
    Restore-Backup -BackupPath $backup
    if (Test-Deployment) { Write-Ok 'Rollback verified healthy.'; exit 0 }
    Write-Err 'Rollback completed but smoke test still failing — investigate manually.'
    exit 1
}

# ── DEPLOY path ───────────────────────────────────────────────────────────────
Invoke-PreDeploy
$backupPath = Backup-CurrentDeployment

try {
    Invoke-ComponentDeploy
}
catch {
    Write-Err "Deploy failed: $($_.Exception.Message)"
    if ($backupPath) {
        Write-Warn2 'Attempting automatic rollback to pre-deploy snapshot...'
        Restore-Backup -BackupPath $backupPath
    }
    exit 1
}

if (Test-Deployment) {
    Write-Ok "Deploy of '$Component' to $TargetNode succeeded and passed smoke test."
    exit 0
}

Write-Err 'Post-deploy smoke test FAILED.'
if ($backupPath) {
    Write-Warn2 'Auto-rolling back to pre-deploy snapshot...'
    Restore-Backup -BackupPath $backupPath
    Write-Warn2 'Rolled back. The previous version is restored; the new build was NOT kept.'
}
else {
    Write-Warn2 'No backup existed (first deploy) — leaving the new build in place for investigation.'
}
exit 1
