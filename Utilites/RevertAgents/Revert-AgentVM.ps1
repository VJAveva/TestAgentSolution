# ==============================================================
#  Revert-AgentVM.ps1  (v1.0)
#  Fleet-panel revert ADAPTER. The maintenance engine invokes the
#  configured revert script as:  script.ps1 <VmName> <Snapshot>
#  (positional). The real vCloud worker Revert-RcloudMachines.ps1
#  instead needs -OrgName / -Machines / -User / -Password and has
#  no snapshot parameter (vCloud VMs keep a single snapshot). This
#  adapter maps the fleet contract onto that worker and resolves
#  credentials from environment variables so no secret is ever
#  stored in appsettings or passed on a command line by the app.
#
#  Credentials (set once on the controller, per user or machine):
#    setx RCLOUD_USER     <vcloud-user>
#    setx RCLOUD_PASSWORD <vcloud-password>
#    setx RCLOUD_ORG      <org>          # optional; default below
#
#  Usage (as the engine calls it):
#    .\Revert-AgentVM.ps1 JVGR2 "Clean-SP2023R2SP1P03"
#
#  Exit codes: 0 = ready, 1 = revert failed, 2 = credentials missing,
#              5 = worker script not found (matches the .bat launcher)
# ==============================================================
param(
    [Parameter(Mandatory, Position = 0)][string]$VmName,
    [Parameter(Position = 1)][string]$Snapshot = "",
    [string]$OrgName  = $(if ($env:RCLOUD_ORG) { $env:RCLOUD_ORG } else { "AppServerPool2" }),
    [string]$User     = $env:RCLOUD_USER,
    [string]$Password = $env:RCLOUD_PASSWORD
)

$ErrorActionPreference = "Stop"

function Info([string]$m) { Write-Host ("[INFO] {0}" -f $m) }
function Fail([string]$m) { Write-Host ("[FAIL] {0}" -f $m) }

Info ("Adapter  : {0}" -f $PSCommandPath)
Info ("VM       : {0}" -f $VmName)
Info ("Org      : {0}" -f $OrgName)
# vCloud keeps a single snapshot per VM, so the name is informational only; the worker reverts to that snapshot.
Info ("Snapshot : {0}" -f $(if ($Snapshot) { $Snapshot } else { "(current)" }))

if ([string]::IsNullOrWhiteSpace($User) -or [string]::IsNullOrWhiteSpace($Password)) {
    Fail "vCloud credentials are not set. Define RCLOUD_USER and RCLOUD_PASSWORD environment variables on the controller."
    exit 2
}

$here   = Split-Path -Parent $PSCommandPath
$worker = Join-Path $here "Revert-RcloudMachines.ps1"
if (-not (Test-Path $worker)) {
    Fail ("Worker script not found: {0}" -f $worker)
    exit 5
}

Info ("Worker   : {0}" -f $worker)

# Delegate to the real vCloud worker. It owns PowerCLI connect, stop/revert/power-on and the readiness poll,
# and returns 0 = all ready / 1 = one or more failed, which the maintenance engine reads as success/failure.
& $worker -OrgName $OrgName -Machines $VmName -User $User -Password $Password
$rc = $LASTEXITCODE

Info ("Worker exit code: {0}" -f $rc)
exit $rc
