#Requires -Version 5.1
<#
.SYNOPSIS
    Cleanly removes the TestAgent gRPC Windows Service.

.DESCRIPTION
    The reverse of Install-TestAgentGrpcService.ps1:
    1. Self-elevates if not run as Administrator.
    2. Exits cleanly if the service does not exist (nothing to do).
    3. Stops the service if it is running.
    4. Deletes the service.
    5. Verifies the service is gone and reports the result.

    Safe to re-run.
#>

# ----------------------- Configuration ------------------------------
$ServiceName = 'TestAgentGrpc'
# --------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# ---- 1. Ensure we are running as Administrator ---------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
            [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Not running as Administrator - relaunching with elevation..." -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`"" `
        -Verb RunAs
    exit
}

# ---- 2. Does the service exist? ------------------------------------
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' is not installed - nothing to remove." -ForegroundColor Green
    exit 0
}

# ---- 3. Stop the service if it is running --------------------------
if ($svc.Status -ne 'Stopped') {
    Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    # Wait for it to actually reach the Stopped state before deleting.
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
}

# ---- 4. Delete the service -----------------------------------------
Write-Host "Deleting service '$ServiceName'..." -ForegroundColor Cyan
sc.exe delete $ServiceName | Out-Null
$rc = $LASTEXITCODE
#   0    = deleted
#   1072 = marked for deletion (a handle is still open; clears on release/reboot)
if ($rc -ne 0 -and $rc -ne 1072) {
    throw "sc.exe delete failed (exit code $rc)."
}

Start-Sleep -Seconds 2   # let the Service Control Manager update

# ---- 5. Verify and report ------------------------------------------
$check = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Write-Host ""
if ($check) {
    Write-Host "Service is marked for deletion but still listed." -ForegroundColor Yellow
    Write-Host "Close Services.msc / the Task Manager Services tab (or reboot) to finish removal." -ForegroundColor Yellow
} else {
    Write-Host "Done. Service '$ServiceName' has been removed." -ForegroundColor Green
}
