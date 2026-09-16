# ==============================================================
#  Vm-Ops.vcloud.ps1  (v1.0)
#  Verb-dispatched vCloud (Rcloud) VM operations for the fleet
#  maintenance engine. PS 5.1 compatible. ASCII-only.
#
#  Contract (called by ScriptBackedVirtualizationProvider):
#    .\Vm-Ops.vcloud.ps1 -Verb <verb> -Vm "a,b,c" [options]
#
#  Verbs:
#    State     -Vm a,b,c
#    PowerOn   -Vm a,b,c
#    PowerOff  -Vm a,b,c [-Graceful true|false]
#    List      -Vm a
#    Create    -Vm a,b,c -Snapshot <name> [-Description <text>]
#    Revert    -Vm a,b,c
#    Delete    -Vm a -SnapshotId <id>
#
#  vCloud specifics this script encodes:
#    - A VM holds exactly ONE snapshot; it is unnamed. -Snapshot is
#      recorded in the description only, and Revert ignores names.
#    - Create SUPERSEDES the existing snapshot in a single call, so
#      the VM is never left without one. Delete is only needed to
#      reclaim the superseded snapshot's space.
#
#  Credentials (same variables the revert worker already uses):
#    RCLOUD_USER, RCLOUD_PASSWORD, RCLOUD_ORG (default AppServerPool2)
#
#  Output: human-readable [TAG] lines, then ONE compact JSON object
#  as the final line. The caller takes the last well-formed JSON
#  object, so log lines above it are safe.
#
#  Exit codes: 0 = every VM succeeded, 1 = one or more failed,
#              2 = credentials missing, 3 = PowerCLI/connect failure
# ==============================================================
param(
    [Parameter(Mandatory)][ValidateSet("State", "PowerOn", "PowerOff", "List", "Create", "Revert", "Delete")]
    [string]$Verb,
    [Parameter(Mandatory)][string]$Vm,
    [string]$Snapshot = "",
    [string]$Description = "",
    [string]$SnapshotId = "",
    [string]$Graceful = "true",
    [string]$OrgName = $(if ($env:RCLOUD_ORG) { $env:RCLOUD_ORG } else { "AppServerPool2" }),
    [string]$User = $env:RCLOUD_USER,
    [string]$Password = $env:RCLOUD_PASSWORD,
    [string]$vCloudURL = "rcloud.dev.wonderware.com",
    [int]$TaskTimeoutMinutes = 10,
    [string]$LogDir = "C:\TestSetup\Logs"
)

$ErrorActionPreference = "Continue"
$script:server = $null

# ---------------- Logging ----------------
if (-not (Test-Path $LogDir)) {
    try { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null } catch { $LogDir = $env:TEMP }
}
$script:LogFile = Join-Path $LogDir ("Vm-Ops-vcloud_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

function Log([string]$msg, [string]$lvl = "INFO") {
    $ts = (Get-Date).ToString("HH:mm:ss")
    $tag = switch ($lvl) { "OK" { "[PASS]" } "FAIL" { "[FAIL]" } "WARN" { "[WARN]" } "STEP" { "[STEP]" } default { "[INFO]" } }
    $line = "[$ts] $tag $msg"
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -ErrorAction SilentlyContinue } catch { }
}

# ---------------- Result accumulation ----------------
# Built as raw JSON strings: PS 5.1's ConvertTo-Json renders a ONE-element array as an object, which would
# break the caller's parser. Emitting the array brackets by hand removes that trap entirely.
$script:results = @()

function Add-Result([string]$name, [bool]$ok, [string]$err = "", [bool]$retryable = $false,
    [string]$power = "", [string]$snapId = "", [string]$snapName = "", $createdUtc = $null, $sizeBytes = $null) {

    $o = [ordered]@{ vm = $name; ok = $ok }
    if ($err)      { $o.error = $err }
    if ($retryable){ $o.retryable = $true }
    if ($power)    { $o.power = $power }
    if ($snapId)   { $o.snapshotId = $snapId }
    if ($snapName) { $o.snapshotName = $snapName }
    if ($createdUtc) { $o.createdUtc = ([datetime]$createdUtc).ToUniversalTime().ToString("o") }
    if ($null -ne $sizeBytes) { $o.sizeBytes = [int64]$sizeBytes }

    $script:results += (New-Object psobject -Property $o | ConvertTo-Json -Depth 4 -Compress)
}

function Emit-And-Exit([int]$code, [string]$topLevelError = "") {
    if ($script:server) {
        try { Disconnect-CIServer -Server $script:server -Confirm:$false -ErrorAction SilentlyContinue } catch { }
    }
    $allOk = if ($code -eq 0) { "true" } else { "false" }
    $errPart = if ($topLevelError) { ',"error":' + (ConvertTo-Json $topLevelError -Compress) } else { "" }
    $json = '{"ok":' + $allOk + ',"platform":"vcloud"' + $errPart + ',"results":[' + ($script:results -join ',') + ']}'
    Write-Host $json
    exit $code
}

function Fail-All([string]$reason, [int]$code) {
    foreach ($n in $script:names) { Add-Result $n $false $reason }
    Log $reason "FAIL"
    Emit-And-Exit $code $reason
}

# ---------------- Parse VM list ----------------
$script:names = @($Vm -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($script:names.Count -eq 0) {
    Write-Host '{"ok":false,"platform":"vcloud","error":"No VM names supplied.","results":[]}'
    exit 1
}

Log "=================================================="
Log "VM-OPS (vCloud) | Verb: $Verb | VMs: $($script:names -join ', ')"
Log "Org: $OrgName | Server: $vCloudURL"
Log "=================================================="

if ([string]::IsNullOrWhiteSpace($User) -or [string]::IsNullOrWhiteSpace($Password)) {
    Fail-All "vCloud credentials are not set. Define RCLOUD_USER and RCLOUD_PASSWORD on the controller." 2
}

# ---------------- PowerCLI self-heal (same strategy as Revert-RcloudMachines.ps1) ----------------
$script:PowerCliCandidates = @("VMware.PowerCLI", "VCF.PowerCLI")

function Import-AnyPowerCli([switch]$SkipEditionCheck) {
    foreach ($m in $script:PowerCliCandidates) {
        try {
            if ($SkipEditionCheck) { Import-Module $m -ErrorAction Stop -SkipEditionCheck }
            else { Import-Module $m -ErrorAction Stop }
            Log "$m $((Get-Module $m).Version) loaded" "OK"
            return $true
        }
        catch { }
    }
    return $false
}

$moduleReady = Import-AnyPowerCli
if (-not $moduleReady -and $PSVersionTable.PSVersion.Major -ge 7) {
    try {
        $winPS = "C:\Program Files\WindowsPowerShell\Modules"
        foreach ($m in $script:PowerCliCandidates) {
            if (Test-Path "$winPS\$m") { $env:PSModulePath = "$winPS;$env:PSModulePath"; break }
        }
        $moduleReady = Import-AnyPowerCli -SkipEditionCheck
    }
    catch { }
}
if (-not $moduleReady) {
    try { Add-PSSnapin VMware.VimAutomation.Cloud -ErrorAction Stop; $moduleReady = $true } catch { }
}
if (-not $moduleReady) {
    Fail-All "PowerCLI could not be loaded. Install with: Install-Module VMware.PowerCLI -Scope AllUsers -Force -AllowClobber" 3
}

# ---------------- Connect once ----------------
try {
    Set-PowerCLIConfiguration -Scope User -ParticipateInCEIP $false -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    Set-PowerCLIConfiguration -Scope User -InvalidCertificateAction Ignore -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}
catch { }

$SecurePassword = ConvertTo-SecureString $Password -AsPlainText -Force
$script:Credentials = New-Object System.Management.Automation.PSCredential($User, $SecurePassword)

for ($i = 1; $i -le 3; $i++) {
    try {
        $script:server = Connect-CIServer -Server $vCloudURL -Org $OrgName -Credential $script:Credentials -ErrorAction Stop -WarningAction SilentlyContinue
        if ($script:server) { Log "Connected as $($script:server.User)" "OK"; break }
    }
    catch {
        if ($i -lt 3) { Log "Connect attempt $i failed: $($_.Exception.Message) - retrying in 10s..." "WARN"; Start-Sleep -Seconds 10 }
        else { Fail-All "Failed to connect after 3 attempts: $($_.Exception.Message)" 3 }
    }
}

# ---------------- Helpers ----------------
function Get-Vm([string]$name) {
    try { return Get-CIVM -Name $name -Server $script:server -ErrorAction Stop | Select-Object -First 1 }
    catch {
        Log "Query for '$name' failed ($($_.Exception.Message)) - reconnecting..." "WARN"
        try {
            $script:server = Connect-CIServer -Server $vCloudURL -Org $OrgName -Credential $script:Credentials -ErrorAction Stop -WarningAction SilentlyContinue
            return Get-CIVM -Name $name -Server $script:server -ErrorAction Stop | Select-Object -First 1
        }
        catch { return $null }
    }
}

function Map-Power($status) {
    switch ("$status") {
        "PoweredOn"  { return "on" }
        "PoweredOff" { return "off" }
        "Suspended"  { return "suspended" }
        default      { return "unknown" }
    }
}

function Is-Retryable([string]$msg) {
    return ($msg -match "BUSY_ENTITY" -or $msg -match "unable to perform" -or $msg -match "is busy")
}

function Wait-EntityIdle($vmObj, [int]$TimeoutSec) {
    # vCloud rejects operations while a task still runs on the VM or its vApp, even after Status flips.
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $running = @()
        try {
            foreach ($entity in @($vmObj.ExtensionData, $vmObj.VApp.ExtensionData)) {
                if ($entity -and $entity.Tasks -and $entity.Tasks.Task) {
                    $running += @($entity.Tasks.Task | Where-Object { $_.Status -in @("running", "queued", "preRunning") })
                }
            }
        }
        catch { return }
        if ($running.Count -eq 0) { return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $end)
}

function Get-CurrentSnapshot($vmObj) {
    try {
        $section = $vmObj.ExtensionData.GetSnapshotSection()
        if ($section -and $section.Snapshot) { return $section.Snapshot }
    }
    catch { }
    return $null
}

# ---------------- Verb dispatch ----------------
$anyFailed = $false

foreach ($name in $script:names) {
    $vmObj = Get-Vm $name
    if (-not $vmObj) {
        Add-Result $name $false "VM not found in org '$OrgName'."
        $anyFailed = $true
        continue
    }

    try {
        switch ($Verb) {

            "State" {
                Add-Result $name $true -power (Map-Power $vmObj.Status)
                Log "$name : $($vmObj.Status)" "OK"
            }

            "List" {
                $snap = Get-CurrentSnapshot $vmObj
                if ($snap) {
                    # vCloud snapshots are unnamed; the creation timestamp is the only identity available.
                    $id = "$($vmObj.Id)/snapshot"
                    Add-Result $name $true -snapId $id -snapName "current" -createdUtc $snap.Created
                    Log "$name : snapshot created $($snap.Created)" "OK"
                }
                else {
                    Add-Result $name $true
                    Log "$name : no snapshot" "OK"
                }
            }

            "PowerOn" {
                if ($vmObj.Status -eq "PoweredOn") {
                    Add-Result $name $true -power "on"
                    Log "$name : already powered on" "OK"
                }
                else {
                    Start-CIVM -VM $vmObj -Confirm:$false -ErrorAction Stop | Out-Null
                    Add-Result $name $true -power "on"
                    Log "$name : power-on issued" "OK"
                }
            }

            "PowerOff" {
                if ($vmObj.Status -eq "PoweredOff") {
                    Add-Result $name $true -power "off"
                    Log "$name : already powered off" "OK"
                }
                else {
                    if ($Graceful -eq "true") {
                        try { Stop-CIVMGuest -VM $vmObj -Confirm:$false -ErrorAction Stop | Out-Null }
                        catch {
                            Log "$name : guest shutdown unavailable ($($_.Exception.Message)); forcing power-off" "WARN"
                            Stop-CIVM -VM $vmObj -Confirm:$false -ErrorAction Stop | Out-Null
                        }
                    }
                    else {
                        Stop-CIVM -VM $vmObj -Confirm:$false -ErrorAction Stop | Out-Null
                    }
                    Wait-EntityIdle $vmObj ($TaskTimeoutMinutes * 60)
                    Add-Result $name $true -power "off"
                    Log "$name : powered off" "OK"
                }
            }

            "Revert" {
                # The snapshot is unnamed and singular, so any -Snapshot value is informational only.
                $snap = Get-CurrentSnapshot $vmObj
                if (-not $snap) { throw "No snapshot exists to revert to." }
                Wait-EntityIdle $vmObj ($TaskTimeoutMinutes * 60)
                $vmObj.ExtensionData.RevertToCurrentSnapshot() | Out-Null
                Add-Result $name $true
                Log "$name : revert issued (snapshot created $($snap.Created))" "OK"
            }

            "Create" {
                # Supersedes the existing snapshot in one call, so the VM is never without a baseline.
                Wait-EntityIdle $vmObj ($TaskTimeoutMinutes * 60)
                $label = if ($Snapshot) { $Snapshot } else { "baseline" }
                $note = if ($Description) { $Description } else { $label }

                $params = New-Object VMware.VimAutomation.Cloud.Views.CreateSnapshotParams
                $params.Memory = $false      # VM is powered off for a baseline; no memory state to capture
                $params.Quiesce = $true
                $params.Name = $label
                $params.Description = $note
                $vmObj.ExtensionData.CreateSnapshot($params) | Out-Null

                $fresh = Get-Vm $name
                $snap = if ($fresh) { Get-CurrentSnapshot $fresh } else { $null }
                $created = if ($snap) { $snap.Created } else { (Get-Date).ToUniversalTime() }
                Add-Result $name $true -snapId "$($vmObj.Id)/snapshot" -snapName $label -createdUtc $created
                Log "$name : snapshot created ($label)" "OK"
            }

            "Delete" {
                $snap = Get-CurrentSnapshot $vmObj
                if (-not $snap) {
                    # Nothing to remove is the desired end state, not a failure.
                    Add-Result $name $true
                    Log "$name : no snapshot to remove" "OK"
                }
                else {
                    Wait-EntityIdle $vmObj ($TaskTimeoutMinutes * 60)
                    $vmObj.ExtensionData.RemoveAllSnapshots() | Out-Null
                    Add-Result $name $true
                    Log "$name : snapshot removed" "OK"
                }
            }
        }
    }
    catch {
        $msg = $_.Exception.Message
        Add-Result $name $false $msg (Is-Retryable $msg)
        Log "$name : $Verb failed - $msg" "FAIL"
        $anyFailed = $true
    }
}

Emit-And-Exit $(if ($anyFailed) { 1 } else { 0 })
