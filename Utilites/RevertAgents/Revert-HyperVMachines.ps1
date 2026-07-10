# ==============================================================
#  Revert-HyperVMachines.ps1  (v2.0)
#  Reverts ONE or MANY Hyper-V VMs to a checkpoint SIMULTANEOUSLY.
#  Runs on the Hyper-V host. PowerShell 5.1 compatible. ASCII-only.
#
#  Design: phased fan-out. Each action (stop / restore / start) is
#  issued to ALL machines first, then a single polling loop waits
#  for all of them together - so 10 VMs take about as long as 1.
#
#  Auto-heal:
#    - VM stuck stopping        -> forced TurnOff retried
#    - Restore fails            -> re-TurnOff + one retry
#    - Start fails              -> retried up to 3 times
#    - Heartbeat never comes up -> ONE forced restart, then keeps waiting
#    - No Heartbeat service     -> falls back to ping (3 consecutive)
#
#  Usage (single):    .\Revert-HyperVMachines.ps1 -VMs HCPCIAGENT7
#  Usage (multiple):  .\Revert-HyperVMachines.ps1 -VMs HCPCIAGENT7,HCPCIAGENT8,HCPCIAGENT9
#  Custom checkpoint: .\Revert-HyperVMachines.ps1 -VMs AG1,AG2 -Checkpoint "SP2026_BASELINE"
#  Default checkpoint per VM is "<VMNAME>_CLEAN" (old script convention).
#
#  Exit codes: 0 = all ready, 1 = one or more failed
# ==============================================================
param(
    [Parameter(Mandatory, Position = 0)][string[]]$VMs,
    [string]$Checkpoint = "",                 # empty = "<VMNAME>_CLEAN" per VM
    [string]$CheckpointSuffix = "_CLEAN",
    [int]$StopTimeoutMinutes  = 4,
    [int]$ReadyTimeoutMinutes = 15,
    [int]$BootSettleSeconds   = 0,            # extra wait after heartbeat OK
    [string]$LogDir = "C:\TestSetup\Logs"
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date

# ---------------- Logging ----------------
if (-not (Test-Path $LogDir)) {
    try { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
    catch { $LogDir = $env:TEMP }
}
$script:LogFile = Join-Path $LogDir ("Revert-HyperV_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

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

# ---------------- Per-VM state ----------------
# Phase: Init -> Stopped -> Reverted -> Started -> Ready | Failed
$states = @()
foreach ($name in $VMs) {
    $cp = if ($Checkpoint) { $Checkpoint } else { "$name$CheckpointSuffix" }
    $states += [pscustomobject]@{
        Name = $name; Checkpoint = $cp; Phase = "Init"
        Failed = $false; Error = ""; Healed = $false; Pings = 0
    }
}

function Fail($s, [string]$reason) {
    $s.Failed = $true; $s.Phase = "Failed"; $s.Error = $reason
    Log "$($s.Name): $reason" "FAIL"
}
function Active() { $states | Where-Object { -not $_.Failed } }

Log "=================================================="
Log "REVERT HYPER-V MACHINES (parallel)"
Log "Host       : $env:COMPUTERNAME"
Log "Machines   : $($VMs -join ', ')"
Log "Checkpoint : $(if ($Checkpoint) { $Checkpoint } else { '<VM>' + $CheckpointSuffix + ' (per VM)' })"
Log "Log file   : $script:LogFile"
Log "=================================================="

# ---------------- Phase 0: Validate ----------------
Log "Validating VMs and checkpoints..." "STEP"
foreach ($s in $states) {
    try {
        $vm = Get-VM -Name $s.Name -ErrorAction Stop
        $snap = Get-VMSnapshot -VMName $s.Name -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $s.Checkpoint }
        if (-not $snap) {
            $avail = (Get-VMSnapshot -VMName $s.Name -ErrorAction SilentlyContinue |
                      Select-Object -ExpandProperty Name) -join ", "
            Fail $s "Checkpoint '$($s.Checkpoint)' not found. Available: $(if ($avail) { $avail } else { 'none' })"
            continue
        }
        Log "$($s.Name): checkpoint '$($s.Checkpoint)' found (state: $($vm.State))" "OK"
    }
    catch { Fail $s "VM not found on this host: $($_.Exception.Message)" }
}
if (-not (Active)) { Log "No valid machines to revert - aborting" "FAIL"; exit 1 }

# ---------------- Phase 1: Stop all ----------------
Log "Turning off all machines..." "STEP"
foreach ($s in Active) {
    try {
        if ((Get-VM -Name $s.Name).State -ne "Off") {
            Stop-VM -Name $s.Name -TurnOff -Force -ErrorAction Stop
            Log "$($s.Name): TurnOff issued"
        } else { Log "$($s.Name): already off" }
    }
    catch { Log "$($s.Name): TurnOff error, will retry in poll: $($_.Exception.Message)" "WARN" }
}

$deadline = (Get-Date).AddMinutes($StopTimeoutMinutes)
do {
    $pending = @(Active | Where-Object { (Get-VM -Name $_.Name).State -ne "Off" })
    if ($pending.Count -eq 0) { break }
    Start-Sleep -Seconds 3
    foreach ($s in $pending) {
        # heal: re-issue forced TurnOff on anything still not off
        try { Stop-VM -Name $s.Name -TurnOff -Force -ErrorAction SilentlyContinue } catch { }
    }
} while ((Get-Date) -lt $deadline)

foreach ($s in Active) {
    if ((Get-VM -Name $s.Name).State -ne "Off") { Fail $s "Did not turn off within $StopTimeoutMinutes min" }
    else { $s.Phase = "Stopped" }
}
Log "$(@(Active).Count) machine(s) off" "OK"

# ---------------- Phase 2: Restore all ----------------
Log "Restoring checkpoints..." "STEP"
foreach ($s in Active) {
    $done = $false
    for ($try = 1; $try -le 2 -and -not $done; $try++) {
        try {
            Restore-VMSnapshot -VMName $s.Name -Name $s.Checkpoint -Confirm:$false -ErrorAction Stop
            $s.Phase = "Reverted"
            Log "$($s.Name): restored '$($s.Checkpoint)'" "OK"
            $done = $true
        }
        catch {
            if ($try -lt 2) {
                Log "$($s.Name): restore failed, healing (TurnOff + retry): $($_.Exception.Message)" "WARN"
                try { Stop-VM -Name $s.Name -TurnOff -Force -ErrorAction SilentlyContinue } catch { }
                Start-Sleep -Seconds 5
            }
            else { Fail $s "Restore failed after retry: $($_.Exception.Message)" }
        }
    }
}
if (-not (Active)) { Log "All machines failed during restore" "FAIL"; exit 1 }

# ---------------- Phase 3: Start all ----------------
Log "Starting all machines..." "STEP"
foreach ($s in Active) {
    $started = $false
    for ($try = 1; $try -le 3 -and -not $started; $try++) {
        try {
            Start-VM -Name $s.Name -ErrorAction Stop
            $s.Phase = "Started"
            Log "$($s.Name): start issued"
            $started = $true
        }
        catch {
            if ($try -lt 3) { Start-Sleep -Seconds 10 }
            else { Fail $s "Start failed after 3 attempts: $($_.Exception.Message)" }
        }
    }
}

# ---------------- Phase 4: Wait for all (heartbeat, ping fallback) ----------------
Log "Waiting for machines to come online (max $ReadyTimeoutMinutes min)..." "STEP"
$deadline  = (Get-Date).AddMinutes($ReadyTimeoutMinutes)
$healPoint = (Get-Date).AddMinutes([math]::Ceiling($ReadyTimeoutMinutes / 2))

while ((Get-Date) -lt $deadline) {
    $waiting = @(Active | Where-Object { $_.Phase -ne "Ready" })
    if ($waiting.Count -eq 0) { break }

    foreach ($s in $waiting) {
        $ready = $false
        try {
            $hb = Get-VMIntegrationService -VMName $s.Name -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -eq "Heartbeat" }
            if ($hb -and $hb.Enabled) {
                if ($hb.PrimaryStatusDescription -eq "OK") { $ready = $true }
            }
            else {
                # heal path: no heartbeat service -> 3 consecutive pings
                if (Test-Connection -ComputerName $s.Name -Count 1 -Quiet -ErrorAction SilentlyContinue) {
                    $s.Pings++
                    if ($s.Pings -ge 3) { $ready = $true }
                } else { $s.Pings = 0 }
            }
        }
        catch { }

        if ($ready) {
            $s.Phase = "Ready"
            $mins = [math]::Round(((Get-Date) - $startTime).TotalMinutes, 1)
            Log "$($s.Name): READY ($mins min)" "OK"
        }
        elseif (-not $s.Healed -and (Get-Date) -gt $healPoint) {
            # heal: one forced restart at the halfway point
            $s.Healed = $true
            Log "$($s.Name): not up at halfway point - healing with forced restart" "WARN"
            try {
                Stop-VM -Name $s.Name -TurnOff -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 5
                Start-VM -Name $s.Name -ErrorAction SilentlyContinue
            } catch { }
        }
    }
    Start-Sleep -Seconds 5
}

foreach ($s in Active) {
    if ($s.Phase -ne "Ready") { Fail $s "No heartbeat/ping within $ReadyTimeoutMinutes min" }
}

if ($BootSettleSeconds -gt 0 -and @(Active).Count -gt 0) {
    Log "Boot settle wait: ${BootSettleSeconds}s..."
    Start-Sleep -Seconds $BootSettleSeconds
}

# ---------------- Summary ----------------
$totalTime = ((Get-Date) - $startTime).ToString("hh\:mm\:ss")
$failedList = @($states | Where-Object { $_.Failed })
Log "=================================================="
Log "SUMMARY  (total time $totalTime)"
foreach ($s in $states) {
    if ($s.Failed) { Log ("  {0,-16} FAILED : {1}" -f $s.Name, $s.Error) "FAIL" }
    else           { Log ("  {0,-16} READY  (checkpoint '{1}')" -f $s.Name, $s.Checkpoint) "OK" }
}
Log "Log file: $script:LogFile"
Log "=================================================="

if ($failedList.Count -eq 0) { Log "ALL $($states.Count) MACHINE(S) READY" "OK"; exit 0 }
else { Log "$($failedList.Count) of $($states.Count) machine(s) FAILED" "FAIL"; exit 1 }
