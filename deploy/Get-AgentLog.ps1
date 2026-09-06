<#
.SYNOPSIS
    Human-friendly viewer for the TestAgentGrpc structured audit log (audit_*.jsonl).

.DESCRIPTION
    The agent already writes a rich, structured audit trail (one JSON object per
    line) covering command execution and lifecycle events:
        CommandStarted / CommandCompleted / CommandTerminated / CommandFailed
        AgentStarted / AgentStopped / RegistrationAcked / RegistrationFailed
        ControllerLost / ControllerRecovered / HeartbeatAcked / HeartbeatFailed

    Raw JSONL is hard to read and heartbeats drown out the signal. This viewer
    parses the file, drops heartbeat noise by default, converts UTC to local
    time, and prints a color-coded table you can scan at a glance.

.PARAMETER ComputerName
    Remote agent node (e.g. JVGR1). Omit to read a local file. Uses the admin
    share (\\<node>\C$) so no remoting session is required.

.PARAMETER LogDirectory
    Audit log directory. Defaults to C:\TestAgentSolution\Logs\audit
    (the agent's AuditSettings.LogDirectory default).

.PARAMETER Date
    Which daily file to read (yyyy-MM-dd). Defaults to the most recent file.

.PARAMETER Tail
    Show only the last N relevant entries. Default 50. Use 0 for all.

.PARAMETER ErrorsOnly
    Show only Warning/Error severity entries (registration failures,
    terminations, controller-lost, command failures).

.PARAMETER Execution
    Filter to a single execution by its ExecutionId (full or partial match).

.PARAMETER IncludeHeartbeats
    Include HeartbeatAcked entries (hidden by default).

.PARAMETER Follow
    Live tail: re-read every 3 seconds (Ctrl+C to stop). Local files only.

.EXAMPLE
    .\Get-AgentLog.ps1 -ComputerName JVGR1
    Last 50 non-heartbeat events from the newest audit file on JVGR1.

.EXAMPLE
    .\Get-AgentLog.ps1 -ComputerName JVGR1 -ErrorsOnly -Tail 0
    Every warning/error in the newest file - the fast path to "what broke".

.EXAMPLE
    .\Get-AgentLog.ps1 -ComputerName JVGR1 -Execution a1b2c3d4
    The full lifecycle of one command execution (started -> output -> completed).
#>
[CmdletBinding()]
param(
    [string]$ComputerName,
    [string]$LogDirectory = 'C:\TestAgentSolution\Logs\audit',
    [string]$Date,
    [int]$Tail = 50,
    [switch]$ErrorsOnly,
    [string]$Execution,
    [switch]$IncludeHeartbeats,
    [switch]$Follow
)

function Resolve-AuditPath {
    param([string]$Computer, [string]$Dir, [string]$DateStr)

    $root = if ($Computer) {
        $drive = ($Dir -replace '^([A-Za-z]):', '$1$')   # C:\... -> C$\...
        "\\$Computer\$drive"
    } else { $Dir }

    if (-not (Test-Path $root)) {
        throw "Audit directory not found: $root"
    }

    if ($DateStr) {
        $file = Join-Path $root "audit_$DateStr.jsonl"
        if (-not (Test-Path $file)) { throw "No audit file for $DateStr at $root" }
        return $file
    }

    $latest = Get-ChildItem $root -Filter 'audit_*.jsonl' -ErrorAction Stop |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw "No audit_*.jsonl files in $root" }
    return $latest.FullName
}

function Read-AuditEntries {
    param([string]$Path)

    Get-Content -Path $Path -ErrorAction Stop | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return }
        try { $_ | ConvertFrom-Json } catch { }   # skip a torn final line
    }
}

function Show-AuditTable {
    param([object[]]$Entries)

    foreach ($e in $Entries) {
        $local = try { ([datetime]$e.timestamp).ToLocalTime().ToString('MM-dd HH:mm:ss') }
                 catch { "$($e.timestamp)" }

        $color = switch ($e.severity) {
            'Error'   { 'Red' }
            'Warning' { 'Yellow' }
            default   {
                switch -Wildcard ($e.event) {
                    'CommandStarted'   { 'Cyan' }
                    'CommandCompleted' { if ($e.exitCode -eq 0) { 'Green' } else { 'Red' } }
                    'Agent*'           { 'Magenta' }
                    default            { 'Gray' }
                }
            }
        }

        $exec = if ($e.executionId) { " [$($e.executionId)]" } else { '' }
        $exit = if ($null -ne $e.exitCode) { " exit=$($e.exitCode)" } else { '' }
        $dur  = if ($null -ne $e.durationMs) { " $($e.durationMs)ms" } else { '' }
        $procId = if ($null -ne $e.pid) { " pid=$($e.pid)" } else { '' }

        $cmd = if ($e.command) {
            $a = if ($e.arguments) { " $($e.arguments)" } else { '' }
            " | $($e.command)$a"
        } else { '' }

        $detail = if ($e.detail) { " | $($e.detail)" } else { '' }

        $line = "{0}  {1,-20}{2}{3}{4}{5}{6}{7}" -f `
                $local, $e.event, $exec, $exit, $dur, $procId, $cmd, $detail
        Write-Host $line -ForegroundColor $color
    }
}

function Get-FilteredEntries {
    param([string]$Path)

    $entries = @(Read-AuditEntries -Path $Path)

    if (-not $IncludeHeartbeats) {
        $entries = $entries | Where-Object { $_.event -ne 'HeartbeatAcked' }
    }
    if ($ErrorsOnly) {
        $entries = $entries | Where-Object { $_.severity -in @('Warning', 'Error') }
    }
    if ($Execution) {
        $entries = $entries | Where-Object { $_.executionId -and $_.executionId -like "*$Execution*" }
    }
    if ($Tail -gt 0) {
        $entries = $entries | Select-Object -Last $Tail
    }
    return $entries
}

# -- Main ----------------------------------------------------------------
$path = Resolve-AuditPath -Computer $ComputerName -Dir $LogDirectory -DateStr $Date
Write-Host "Audit log: $path" -ForegroundColor DarkGray
Write-Host ("-" * 100) -ForegroundColor DarkGray

if ($Follow) {
    if ($ComputerName) { throw "-Follow is local-only (UNC tailing is unreliable). Run it on the node." }
    $seen = 0
    while ($true) {
        $all = @(Get-FilteredEntries -Path $path)
        if ($all.Count -gt $seen) {
            Show-AuditTable -Entries ($all | Select-Object -Skip $seen)
            $seen = $all.Count
        }
        Start-Sleep -Seconds 3
    }
} else {
    Show-AuditTable -Entries (Get-FilteredEntries -Path $path)
}
