<#
.SYNOPSIS
  Watches the TestAgent gRPC listener (port 5200) during an install and records why it stops.
  Runs independently of the agent (start it as a SYSTEM scheduled task). Read-only: changes nothing.

  Output folder (default C:\AgentMonitor):
    monitor_<time>.csv        one row every IntervalSeconds
    monitor_<time>.log        state changes in plain words
    snapshot_<time>.txt       full detail captured each time the listener stops or recovers

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File Watch-AgentListener.ps1 [-Minutes 120] [-IntervalSeconds 10]
#>
param(
    [int]$Minutes = 120,
    [int]$IntervalSeconds = 10,
    [int]$Port = 5200,
    [string]$OutDir = 'C:\AgentMonitor',
    # Measured on jvkpri 2026-10-04: no single path is right. C:\TestAgentService\Logs exists but is EMPTY,
    # C:\TestControllerService\Logs (AppLogger.DefaultLogDirectory, never overridden by the agent) holds only
    # agent_crash.log, and the live log is the audit jsonl. Search all of them and take whatever is freshest.
    [string[]]$AgentLogDirs = @(
        'C:\TestControllerService\Logs',
        'C:\TestAgentService\Logs',
        'C:\TestAgentSolution\Logs\audit'
    )
)

$ErrorActionPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$csv   = Join-Path $OutDir ("monitor_{0}.csv" -f $stamp)
$txt   = Join-Path $OutDir ("monitor_{0}.log" -f $stamp)

function Write-Note([string]$m) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m
    Add-Content -Path $txt -Value $line
}

function Test-LocalPort([int]$p) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $ar = $c.BeginConnect('127.0.0.1', $p, $null, $null)
        $ok = $ar.AsyncWaitHandle.WaitOne(1500)
        if ($ok -and $c.Connected) { return $true }
        return $false
    } catch { return $false } finally { $c.Close() }
}

# Newest agent-written file across every candidate folder, so a relocated or renamed log is still found.
function Get-AgentLogFiles([int]$Count = 2) {
    $pattern = '^(agent_|app_|errors_|audit_|agent_crash)'
    Get-ChildItem $AgentLogDirs -File -EA SilentlyContinue |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $Count
}

# Windows names the process that asked for a restart (1074). That is what distinguishes a patch-agent
# reboot from ours - on jvkpri it was ManageEngine dcmsghandler.exe 6 s before the listener vanished.
function Get-RestartInitiators([int]$Minutes = 15) {
    $ev = Get-WinEvent -FilterHashtable @{
        LogName = 'System'; Id = 1074, 1076, 6006, 6008; StartTime = (Get-Date).AddMinutes(-$Minutes)
    } -ErrorAction SilentlyContinue
    $ev | Sort-Object TimeCreated | ForEach-Object {
        '{0:HH:mm:ss} Id={1} {2}' -f $_.TimeCreated, $_.Id, (($_.Message -replace "`r?`n", ' ').Trim())
    }
}

function Save-Snapshot([string]$reason) {
    $f = Join-Path $OutDir ("snapshot_{0:yyyyMMdd_HHmmss}.txt" -f (Get-Date))
    $out = New-Object System.Collections.ArrayList
    [void]$out.Add("=== SNAPSHOT: $reason  ($(Get-Date)) ===")
    [void]$out.Add("`n--- Listeners / connections on port $Port ---")
    [void]$out.Add((netstat -ano | Select-String (":$Port\s") | Out-String))
    [void]$out.Add("`n--- TestAgentGrpc process(es) ---")
    [void]$out.Add((Get-Process TestAgentGrpc | Select-Object Id, StartTime, CPU, Handles, Threads, WorkingSet64, Responding | Format-List | Out-String))
    [void]$out.Add("`n--- Agent scheduled task(s) ---")
    [void]$out.Add((Get-ScheduledTask | Where-Object TaskName -like '*TestAgent*' | Select-Object TaskName, State | Out-String))
    [void]$out.Add("`n--- Installer / setup processes ---")
    [void]$out.Add((Get-Process | Where-Object { $_.ProcessName -match 'msiexec|setup|install|configurator|aa|archestra|dotnet|vc_?redist' } | Select-Object Id, ProcessName, StartTime, Path | Format-Table -AutoSize | Out-String -Width 250))
    [void]$out.Add("`n--- Network profile ---")
    [void]$out.Add((Get-NetConnectionProfile | Select-Object InterfaceAlias, NetworkCategory, IPv4Connectivity | Out-String))
    [void]$out.Add("`n--- Firewall Block rules for testagentgrpc ---")
    [void]$out.Add((Get-NetFirewallRule -Action Block | Get-NetFirewallApplicationFilter | Where-Object Program -like '*testagentgrpc*' | Select-Object Program | Out-String))
    [void]$out.Add("`n--- HTTP.sys / port reservations touching $Port ---")
    [void]$out.Add((netsh http show urlacl | Select-String "$Port" | Out-String))
    [void]$out.Add((netsh int ipv4 show excludedportrange tcp | Out-String))
    [void]$out.Add("`n--- Event log, last 10 minutes (System + Application, warnings and errors) ---")
    $since = (Get-Date).AddMinutes(-10)
    foreach ($ln in 'System', 'Application') {
        [void]$out.Add((Get-WinEvent -FilterHashtable @{ LogName = $ln; StartTime = $since; Level = 1, 2, 3 } |
            Select-Object TimeCreated, ProviderName, Id, @{ n = 'Message'; e = { ($_.Message -split "`n")[0] } } |
            Format-Table -AutoSize -Wrap | Out-String -Width 250))
    }
    [void]$out.Add("`n--- MsiInstaller events, last 30 minutes ---")
    [void]$out.Add((Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MsiInstaller'; StartTime = (Get-Date).AddMinutes(-30) } |
        Select-Object TimeCreated, Id, @{ n = 'Message'; e = { ($_.Message -split "`n")[0] } } | Format-Table -AutoSize -Wrap | Out-String -Width 250))
    [void]$out.Add("`n--- Who asked for a restart (last 15 min) ---")
    $init = Get-RestartInitiators 15
    if ($init) { $init | ForEach-Object { [void]$out.Add($_) } } else { [void]$out.Add('(no restart/shutdown events)') }
    [void]$out.Add("`n--- Agent log, last 60 lines ---")
    $lf = Get-AgentLogFiles 1
    if ($lf) { [void]$out.Add("File: $($lf.FullName)"); [void]$out.Add((Get-Content $lf.FullName -Tail 60 | Out-String)) }
    else { [void]$out.Add("No agent log found under: $($AgentLogDirs -join '; ')") }
    $out | Set-Content -Path $f -Encoding ASCII
    Write-Note ("Snapshot saved: {0}" -f $f)
}

'Time,ListenerUp,LocalConnectOk,ListenerPid,AgentPids,AgentHandles,AgentThreads,AgentMB,Established,InstallerProcs,NetCategory' | Set-Content -Path $csv -Encoding ASCII
Write-Note ("Monitor started. Port {0}, every {1}s, for {2} min. Output: {3}" -f $Port, $IntervalSeconds, $Minutes, $OutDir)
$resolved = Get-AgentLogFiles 2
if ($resolved) { Write-Note ("Agent log resolved: {0}" -f (($resolved | ForEach-Object { $_.FullName }) -join '; ')) }
else { Write-Note ("WARNING: no agent log found under: {0}" -f ($AgentLogDirs -join '; ')) }
Save-Snapshot 'baseline at start'

$end = (Get-Date).AddMinutes($Minutes)
$prevUp = $null
while ((Get-Date) -lt $end) {
    $listen = @(Get-NetTCPConnection -LocalPort $Port -State Listen)
    $up     = $listen.Count -gt 0
    $connOk = Test-LocalPort $Port
    $lpid   = ($listen | Select-Object -First 1).OwningProcess
    $procs  = @(Get-Process TestAgentGrpc)
    $apids  = ($procs | ForEach-Object { $_.Id }) -join ' '
    $h      = ($procs | Measure-Object Handles -Sum).Sum
    $t      = ($procs | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum
    $mb     = [math]::Round((($procs | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 0)
    $est    = @(Get-NetTCPConnection -LocalPort $Port -State Established).Count
    $inst   = (Get-Process | Where-Object { $_.ProcessName -match 'msiexec|setup|configurator' } | ForEach-Object { $_.ProcessName } | Sort-Object -Unique) -join ' '
    $net    = (Get-NetConnectionProfile | Select-Object -First 1).NetworkCategory

    $row = '{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}' -f (Get-Date), $up, $connOk, $lpid, $apids, $h, $t, $mb, $est, $inst, $net
    Add-Content -Path $csv -Value $row

    $healthy = $up -and $connOk
    if ($prevUp -ne $null -and $healthy -ne $prevUp) {
        if ($healthy) { Write-Note "LISTENER RECOVERED (port $Port answering again)"; Save-Snapshot 'listener recovered' }
        else {
            Write-Note ("LISTENER LOST - listening={0} connect={1} agentPids=[{2}] installers=[{3}] network={4}" -f $up, $connOk, $apids, $inst, $net)
            # Written before the snapshot on purpose: a patch-agent reboot kills the box mid-snapshot, so the
            # cheap line is the only thing that survives. Measured on jvkpri - the 'listener lost' snapshot
            # was never created, while this note was.
            foreach ($i in (Get-RestartInitiators 15)) { Write-Note ("  restart-event: {0}" -f $i) }
            Save-Snapshot 'listener lost'
        }
    }
    if ($procs.Count -eq 0 -and $prevUp) { Write-Note 'AGENT PROCESS GONE' }
    $prevUp = $healthy
    Start-Sleep -Seconds $IntervalSeconds
}
Write-Note 'Monitor finished.'
Save-Snapshot 'end of monitoring'
