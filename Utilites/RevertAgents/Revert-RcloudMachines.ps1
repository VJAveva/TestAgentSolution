# ==============================================================
#  Revert-RcloudMachines.ps1  (v2.0)
#  Reverts ONE or MANY vCloud (Rcloud) machines SIMULTANEOUSLY
#  over a SINGLE PowerCLI session. PS 5.1 compatible. ASCII-only.
#
#  Design: phased fan-out. Stop is issued to ALL machines (async),
#  then reverts, then power-on, each followed by ONE polling loop
#  that watches every machine together - 10 VMs cost about the
#  same wall time as 1. Importing PowerCLI and connecting happens
#  exactly once (this was the slowest part of the old per-machine
#  script).
#
#  Auto-heal:
#    - PowerCLI missing        -> auto-install (NuGet/PSGallery),
#                                 PS7 edition fallback, PSSnapin fallback
#    - Connect fails           -> 3 attempts, 10s apart
#    - Session drops mid-run   -> automatic reconnect on next query
#    - Stop/revert/power-on    -> per-machine retries; one failure
#                                 never aborts the other machines
#    - Ping lost during boot   -> consecutive-ping counter resets,
#                                 keeps waiting until deadline
#
#  Usage (single):
#    .\Revert-RcloudMachines.ps1 -OrgName MyOrg -Machines AGENT01 -User u -Password p
#  Usage (multiple):
#    .\Revert-RcloudMachines.ps1 -OrgName MyOrg -Machines AGENT01,AGENT02,AGENT03 -User u -Password p
#
#  Exit codes: 0 = all ready, 1 = one or more failed
# ==============================================================
param(
    [Parameter(Mandatory, Position = 0)][string]$OrgName,
    [Parameter(Mandatory, Position = 1)][string[]]$Machines,
    [Parameter(Mandatory, Position = 2)][string]$User,
    [Parameter(Mandatory, Position = 3)][string]$Password,
    [string]$vCloudURL = "rcloud.dev.wonderware.com",
    [int]$ReadyWaitSeconds = 120,      # settle time after ping OK
    [int]$StopTimeoutMinutes = 5,
    [int]$RevertTimeoutMinutes = 10,
    [int]$ReadyTimeoutMinutes = 15,
    [string]$LogDir = "C:\TestSetup\Logs"
)

$ErrorActionPreference = "Continue"
$startTime = Get-Date
$script:server = $null

# ---------------- Logging ----------------
if (-not (Test-Path $LogDir)) {
    try { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    catch { $LogDir = $env:TEMP }
}
$script:LogFile = Join-Path $LogDir ("Revert-Rcloud_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

function Log([string]$msg, [string]$lvl = "INFO") {
    $ts = (Get-Date).ToString("HH:mm:ss")
    $tag = switch ($lvl) {
        "OK"   { "[PASS]" } "FAIL" { "[FAIL]" } "WARN" { "[WARN]" } "STEP" { "[STEP]" }
        default { "[INFO]" }
    }
    $line = "[$ts] $tag $msg"
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -ErrorAction SilentlyContinue } catch { }
}

function Cleanup {
    if ($script:server) {
        try { Disconnect-CIServer -Server $script:server -Confirm:$false -ErrorAction SilentlyContinue } catch { }
        $script:server = $null
        Log "Disconnected from vCloud"
    }
}

function AbortAll([string]$msg) { Log $msg "FAIL"; Cleanup; exit 1 }

# ---------------- Per-machine state ----------------
$states = @()
foreach ($name in $Machines) {
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $states += [pscustomobject]@{
        Name = $name.Trim(); VApp = ""; Phase = "Init"
        Failed = $false; Error = ""; Pings = 0; ReadyAt = $null
    }
}
if ($states.Count -eq 0) { Write-Host "[FAIL] No machine names provided"; exit 1 }

function Fail($s, [string]$reason) {
    $s.Failed = $true; $s.Phase = "Failed"; $s.Error = $reason
    Log "$($s.Name): $reason" "FAIL"
}
function Active() { $states | Where-Object { -not $_.Failed } }

function Get-VMState($s) {
    # Auto-heal: reconnect if the session dropped
    try {
        return Get-CIVM -Name $s.Name -Server $script:server -ErrorAction Stop | Select-Object -First 1
    }
    catch {
        Log "Session query failed ($($_.Exception.Message)) - reconnecting..." "WARN"
        try {
            $script:server = Connect-CIServer -Server $vCloudURL -Org $OrgName -Credential $script:Credentials -ErrorAction Stop -WarningAction SilentlyContinue
            return Get-CIVM -Name $s.Name -Server $script:server -ErrorAction Stop | Select-Object -First 1
        }
        catch { return $null }
    }
}

function Wait-EntityIdle($s, [int]$TimeoutSec = 180) {
    # vCloud rejects operations while a task (e.g. VAPP_UNDEPLOY_POWER_OFF)
    # is still running on the VM or its vApp, even after Status flips to
    # PoweredOff. Poll until no running/queued tasks remain.
    $end = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $vm = Get-VMState $s
        if (-not $vm) { return }
        $running = @()
        try {
            foreach ($entity in @($vm.ExtensionData, $vm.VApp.ExtensionData)) {
                if ($entity -and $entity.Tasks -and $entity.Tasks.Task) {
                    $running += @($entity.Tasks.Task | Where-Object {
                        $_.Status -in @("running", "queued", "preRunning")
                    })
                }
            }
        }
        catch { }
        if ($running.Count -eq 0) { return }
        Log "$($s.Name): waiting for $($running.Count) running task(s) [$($running[0].OperationName)]..."
        Start-Sleep -Seconds 10
    } while ((Get-Date) -lt $end)
    Log "$($s.Name): entity still busy after ${TimeoutSec}s - proceeding anyway" "WARN"
}

Log "=================================================="
Log "REVERT RCLOUD MACHINES (parallel, single session)"
Log "Organization : $OrgName"
Log "Machines     : $(($states | ForEach-Object { $_.Name }) -join ', ')"
Log "vCloud       : $vCloudURL"
Log "PowerShell   : $($PSVersionTable.PSVersion)"
Log "Log file     : $script:LogFile"
Log "=================================================="

# ---------------- Step 0: Self-heal PowerCLI ----------------
# Accepts classic VMware.PowerCLI (13.x) OR rebranded VCF.PowerCLI (9.x+).
# Both provide Connect-CIServer / Get-CIVM / Stop-CIVM / Start-CIVM.
$script:PowerCliCandidates = @("VMware.PowerCLI", "VCF.PowerCLI")

function Import-AnyPowerCli([switch]$SkipEditionCheck) {
    foreach ($m in $script:PowerCliCandidates) {
        try {
            if ($SkipEditionCheck) { Import-Module $m -ErrorAction Stop -SkipEditionCheck }
            else                   { Import-Module $m -ErrorAction Stop }
            Log "$m $((Get-Module $m).Version) loaded" "OK"
            return $true
        }
        catch { }
    }
    return $false
}

Log "Checking PowerCLI module (VMware.PowerCLI / VCF.PowerCLI)..." "STEP"
$moduleReady = Import-AnyPowerCli
if (-not $moduleReady) { Log "PowerCLI not loaded, attempting self-heal..." "WARN" }

if (-not $moduleReady) {
    try {
        # TLS 1.2 is required for PSGallery on Windows PowerShell 5.1
        [Net.ServicePointManager]::SecurityProtocol = `
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

        # AllUsers when elevated so service accounts can also see the
        # module (CurrentUser installs land in a profile the pipeline
        # service account cannot resolve). CurrentUser otherwise.
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $scope = if ($isAdmin) { "AllUsers" } else { "CurrentUser" }
        Log "Installing PowerCLI from PSGallery (scope: $scope)..."

        $nuget = Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue
        if (-not $nuget -or $nuget.Version -lt [Version]"2.8.5.201") {
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope $scope | Out-Null
        }
        $repo = Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
        if ($repo -and $repo.InstallationPolicy -ne "Trusted") {
            Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
        }

        $installed = $false
        foreach ($m in $script:PowerCliCandidates) {
            try {
                Install-Module $m -Scope $scope -Force -AllowClobber -ErrorAction Stop
                Log "$m installed from PSGallery" "OK"
                $installed = $true
                break
            }
            catch { Log "Install of $m failed: $($_.Exception.Message)" "WARN" }
        }
        if ($installed) { $moduleReady = Import-AnyPowerCli }
    }
    catch { Log "Auto-install failed: $($_.Exception.Message)" "WARN" }
}

if (-not $moduleReady -and $PSVersionTable.PSVersion.Major -ge 7) {
    # PS7 heal: borrow the module from the Windows PowerShell 5.1 path
    try {
        $winPS = "C:\Program Files\WindowsPowerShell\Modules"
        $found = $false
        foreach ($m in $script:PowerCliCandidates) {
            if (Test-Path "$winPS\$m") { $found = $true }
        }
        if ($found) {
            $env:PSModulePath = "$winPS;$env:PSModulePath"
            $moduleReady = Import-AnyPowerCli -SkipEditionCheck
            if ($moduleReady) { Log "Loaded from Windows PS 5.1 module path" "OK" }
        }
    }
    catch { }
}

if (-not $moduleReady) {
    try { Add-PSSnapin VMware.VimAutomation.Cloud -ErrorAction Stop; $moduleReady = $true } catch { }
}
if (-not $moduleReady) {
    AbortAll "PowerCLI could not be loaded. Manual install: run 'Install-Module VMware.PowerCLI -Scope AllUsers -Force -AllowClobber' as admin, or download the offline ZIP from https://developer.broadcom.com/tools/vmware-powercli/latest"
}

# ---------------- Step 1: Connect (once) ----------------
Log "Connecting to vCloud..." "STEP"
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
        if ($script:server) { Log "Connected to $vCloudURL as $($script:server.User)" "OK"; break }
    }
    catch {
        if ($i -lt 3) { Log "Connect attempt $i failed: $($_.Exception.Message) - retrying in 10s..." "WARN"; Start-Sleep -Seconds 10 }
        else { AbortAll "Failed to connect after 3 attempts: $($_.Exception.Message)" }
    }
}

# ---------------- Step 2: Find all VMs ----------------
Log "Finding machines..." "STEP"
foreach ($s in $states) {
    $vm = Get-VMState $s
    if (-not $vm) { Fail $s "VM not found in org '$OrgName'"; continue }
    $s.VApp = $vm.VApp.Name
    Log "$($s.Name): found | vApp: $($s.VApp) | status: $($vm.Status)" "OK"
}
if (-not (Active)) { AbortAll "No machines found - nothing to revert" }

# ---------------- Step 3: Stop ALL (async), then wait together ----------------
Log "Stopping all machines..." "STEP"
$stopTasks = @{}
foreach ($s in Active) {
    $vm = Get-VMState $s
    if ($vm -and $vm.Status -eq "PoweredOn") {
        try {
            $t = Stop-CIVM -VM $vm -Confirm:$false -RunAsync -ErrorAction Stop
            if ($t) { $stopTasks[$s.Name] = $t }
            Log "$($s.Name): stop issued"
        }
        catch { Log "$($s.Name): stop error, will retry in poll: $($_.Exception.Message)" "WARN" }
    }
    else { Log "$($s.Name): already stopped ($(if ($vm) { $vm.Status } else { 'unknown' }))" }
}

$deadline = (Get-Date).AddMinutes($StopTimeoutMinutes)
$stillOn = @()
do {
    Start-Sleep -Seconds 10
    $stillOn = @()
    foreach ($s in Active) {
        $vm = Get-VMState $s
        if ($vm -and $vm.Status -eq "PoweredOn") {
            $stillOn += $s
            # heal: re-issue stop
            try { Stop-CIVM -VM $vm -Confirm:$false -RunAsync -ErrorAction SilentlyContinue | Out-Null } catch { }
        }
    }
    if ($stillOn.Count -gt 0) { Log "Waiting for stop: $(($stillOn | ForEach-Object { $_.Name }) -join ', ')" }
} while ($stillOn.Count -gt 0 -and (Get-Date) -lt $deadline)

foreach ($s in Active) {
    $vm = Get-VMState $s
    if ($vm -and $vm.Status -eq "PoweredOn") { Fail $s "Did not stop within $StopTimeoutMinutes min" }
    else { $s.Phase = "Stopped" }
}
Log "$(@(Active).Count) machine(s) stopped" "OK"
if (-not (Active)) { AbortAll "All machines failed during stop" }

# Status flips to PoweredOff while VAPP_UNDEPLOY_POWER_OFF is still
# running; a revert issued at that moment fails with "Unable to
# perform this action". Wait for stop tasks and entity idle first.
Log "Waiting for undeploy tasks to complete..." "STEP"
foreach ($s in Active) {
    if ($stopTasks.ContainsKey($s.Name)) {
        try { Wait-Task -Task $stopTasks[$s.Name] -ErrorAction SilentlyContinue | Out-Null } catch { }
    }
    Wait-EntityIdle $s
}
Log "All machines idle - safe to revert" "OK"

# ---------------- Step 4: Revert ALL, then wait together ----------------
Log "Reverting all machines to current snapshot..." "STEP"
foreach ($s in Active) {
    $sent = $false
    for ($try = 1; $try -le 4 -and -not $sent; $try++) {
        try {
            $vm = Get-VMState $s
            if (-not $vm) { throw "VM query returned nothing" }
            $snapSection = $vm.ExtensionData.GetSnapshotSection()
            if (-not $snapSection -or -not $snapSection.Snapshot) { Fail $s "No snapshot exists"; break }
            $vm.ExtensionData.RevertToCurrentSnapshot() | Out-Null
            Log "$($s.Name): revert issued (snapshot created $($snapSection.Snapshot.Created))"
            $sent = $true
        }
        catch {
            $err = $_.Exception.Message
            $busy = $err -match "Unable to perform this action|VAPP_UNDEPLOY|is busy|BUSY_ENTITY"
            if ($try -lt 4) {
                if ($busy) {
                    Log "$($s.Name): entity busy (attempt $try/4) - waiting for tasks to clear..." "WARN"
                    Start-Sleep -Seconds 20
                    Wait-EntityIdle $s
                }
                else {
                    Log "$($s.Name): revert failed (attempt $try/4), retrying in 15s: $err" "WARN"
                    Start-Sleep -Seconds 15
                }
            }
            else { Fail $s "Revert failed after 4 attempts: $err" }
        }
    }
}
if (-not (Active)) { AbortAll "All machines failed during revert" }

$deadline = (Get-Date).AddMinutes($RevertTimeoutMinutes)
$busy = @()
do {
    Start-Sleep -Seconds 15
    $busy = @()
    foreach ($s in @(Active | Where-Object { $_.Phase -eq "Stopped" })) {
        $vm = Get-VMState $s
        if ($vm -and $vm.Status -in @("PoweredOff", "Stopped", "Resolved")) {
            $s.Phase = "Reverted"
            Log "$($s.Name): revert complete" "OK"
        }
        else { $busy += $s }
    }
    if ($busy.Count -gt 0) { Log "Reverting: $(($busy | ForEach-Object { $_.Name }) -join ', ')" }
} while ($busy.Count -gt 0 -and (Get-Date) -lt $deadline)

foreach ($s in @(Active | Where-Object { $_.Phase -eq "Stopped" })) {
    # heal: revert command was accepted; proceed to power-on anyway
    Log "$($s.Name): revert status unconfirmed after $RevertTimeoutMinutes min - proceeding to power on" "WARN"
    $s.Phase = "Reverted"
}

# ---------------- Step 5: Power on ALL ----------------
Log "Powering on all machines..." "STEP"
foreach ($s in Active) {
    $started = $false
    for ($try = 1; $try -le 3 -and -not $started; $try++) {
        try {
            $vm = Get-VMState $s
            if (-not $vm) { throw "VM query returned nothing" }
            Start-CIVM -VM $vm -Confirm:$false -RunAsync -ErrorAction Stop | Out-Null
            $s.Phase = "Started"
            Log "$($s.Name): power on issued" "OK"
            $started = $true
        }
        catch {
            $err = $_.Exception.Message
            if ($err -match "already powered on|is running") { $s.Phase = "Started"; Log "$($s.Name): already running" "OK"; $started = $true }
            elseif ($try -lt 3) { Log "$($s.Name): power on attempt $try failed: $err - retrying..." "WARN"; Start-Sleep -Seconds 10 }
            else { Fail $s "Power on failed after 3 attempts: $err" }
        }
    }
}

# ---------------- Step 6: Disconnect (ping wait does not need the session) ----
Cleanup

# ---------------- Step 7: Wait for ALL machines via ping ----------------
Log "Waiting for machines to respond (3 consecutive pings + ${ReadyWaitSeconds}s settle, max $ReadyTimeoutMinutes min)..." "STEP"
$deadline = (Get-Date).AddMinutes($ReadyTimeoutMinutes)

while ((Get-Date) -lt $deadline) {
    $waiting = @(Active | Where-Object { $_.Phase -ne "Ready" })
    if ($waiting.Count -eq 0) { break }
    Start-Sleep -Seconds 15

    foreach ($s in $waiting) {
        if ($s.ReadyAt) {
            # in settle window; when it elapses, confirm the machine stayed up
            if ((Get-Date) -ge $s.ReadyAt) {
                if (Test-Connection -ComputerName $s.Name -Count 3 -Quiet -ErrorAction SilentlyContinue) {
                    $s.Phase = "Ready"
                    $mins = [math]::Round(((Get-Date) - $startTime).TotalMinutes, 1)
                    Log "$($s.Name): READY ($mins min)" "OK"
                }
                else {
                    Log "$($s.Name): went offline during settle - restarting check" "WARN"
                    $s.Pings = 0; $s.ReadyAt = $null
                }
            }
            continue
        }
        if (Test-Connection -ComputerName $s.Name -Count 1 -Quiet -ErrorAction SilentlyContinue) {
            $s.Pings++
            if ($s.Pings -eq 1) { Log "$($s.Name): first ping response" "OK" }
            if ($s.Pings -ge 3) {
                Log "$($s.Name): online - settling ${ReadyWaitSeconds}s"
                $s.ReadyAt = (Get-Date).AddSeconds($ReadyWaitSeconds)
            }
        }
        else {
            if ($s.Pings -gt 0) { Log "$($s.Name): ping lost after $($s.Pings) responses - may be rebooting" "WARN" }
            $s.Pings = 0
        }
    }
}

foreach ($s in Active) {
    if ($s.Phase -ne "Ready") { Fail $s "Did not come online within $ReadyTimeoutMinutes min" }
}

# ---------------- Summary ----------------
$totalTime = ((Get-Date) - $startTime).ToString("hh\:mm\:ss")
$failedList = @($states | Where-Object { $_.Failed })
Log "=================================================="
Log "SUMMARY  (total time $totalTime)"
foreach ($s in $states) {
    if ($s.Failed) { Log ("  {0,-16} FAILED : {1}" -f $s.Name, $s.Error) "FAIL" }
    else           { Log ("  {0,-16} READY  (vApp: {1})" -f $s.Name, $s.VApp) "OK" }
}
Log "Log file: $script:LogFile"
Log "=================================================="

if ($failedList.Count -eq 0) { Log "ALL $($states.Count) MACHINE(S) READY" "OK"; exit 0 }
else { Log "$($failedList.Count) of $($states.Count) machine(s) FAILED" "FAIL"; exit 1 }
