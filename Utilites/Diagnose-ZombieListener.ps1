<#
.SYNOPSIS
  Mines existing logs on a TestAgent machine to find evidence of WHY the gRPC
  listener went Unavailable. Run AFTER an incident, pointing at the time window
  when the controller saw the agent go Unavailable.

.PARAMETER IncidentTime
  Approximate time the agent went Unavailable (from controller logs).
  Format: "2026-06-15 21:56:00"

.PARAMETER WindowMinutes
  How many minutes BEFORE the incident to examine (default 15).

.EXAMPLE
  .\Diagnose-ZombieListener.ps1 -IncidentTime "2026-06-15 21:56:00" -WindowMinutes 15
#>

param(
    [Parameter(Mandatory=$true)]
    [datetime]$IncidentTime,
    [int]$WindowMinutes = 15,
    [string]$AgentLogDir = "C:\TestAgentService\Logs",
    [int]$Port = 5200
)

$ErrorActionPreference = "Continue"
$windowStart = $IncidentTime.AddMinutes(-$WindowMinutes)
$windowEnd   = $IncidentTime.AddMinutes(2)

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Zombie Listener Diagnostic" -ForegroundColor Cyan
Write-Host " Incident time : $IncidentTime" -ForegroundColor Cyan
Write-Host " Window        : $windowStart -> $windowEnd" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------
# 1. AGENT APPLICATION LOG — what was the agent doing before it died?
# ----------------------------------------------------------------------
Write-Host "`n[1] AGENT APPLICATION LOG (window around incident)" -ForegroundColor Yellow
$logFile = Get-ChildItem "$AgentLogDir\*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($logFile) {
    Write-Host "Reading: $($logFile.FullName)"
    Get-Content $logFile.FullName | Where-Object {
        if ($_ -match '(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})') {
            try {
                $ts = [datetime]::Parse($matches[1])
                return ($ts -ge $windowStart -and $ts -le $windowEnd)
            } catch { return $false }
        }
        return $false
    } | Select-Object -Last 100
} else {
    Write-Host "  No agent log files found in $AgentLogDir" -ForegroundColor Red
}

# Look specifically for the last thing logged before silence
Write-Host "`n[1b] LAST 5 LINES BEFORE incident time (the 'final words')" -ForegroundColor Yellow
if ($logFile) {
    Get-Content $logFile.FullName | Where-Object {
        if ($_ -match '(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})') {
            try {
                $ts = [datetime]::Parse($matches[1])
                return ($ts -le $IncidentTime)
            } catch { return $false }
        }
        return $false
    } | Select-Object -Last 5
}

# ----------------------------------------------------------------------
# 2. WINDOWS APPLICATION EVENT LOG — .NET exceptions, crashes, warnings
# ----------------------------------------------------------------------
Write-Host "`n[2] WINDOWS APPLICATION EVENT LOG (.NET Runtime, errors)" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{
    LogName   = 'Application'
    StartTime = $windowStart
    EndTime   = $windowEnd
} -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -in 'Error','Warning','Critical' } |
    Select-Object TimeCreated, LevelDisplayName, ProviderName,
        @{N='Message';E={$_.Message.Substring(0,[Math]::Min(200,$_.Message.Length))}} |
    Format-Table -AutoSize -Wrap

# ----------------------------------------------------------------------
# 3. .NET RUNTIME crashes specifically (EventID 1026 = unhandled exception)
# ----------------------------------------------------------------------
Write-Host "`n[3] .NET RUNTIME EXCEPTIONS (EventID 1026)" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{
    LogName   = 'Application'
    ProviderName = '.NET Runtime'
    StartTime = $windowStart
    EndTime   = $windowEnd
} -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, Id,
        @{N='Message';E={$_.Message.Substring(0,[Math]::Min(400,$_.Message.Length))}} |
    Format-List

# ----------------------------------------------------------------------
# 4. SYSTEM EVENT LOG — service stops, resource exhaustion
# ----------------------------------------------------------------------
Write-Host "`n[4] SYSTEM EVENT LOG (service + resource events)" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{
    LogName   = 'System'
    StartTime = $windowStart
    EndTime   = $windowEnd
} -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Message -match 'TestAgent|memory|resource|Service Control|2004|2019|2020'
    } |
    Select-Object TimeCreated, LevelDisplayName, Id,
        @{N='Message';E={$_.Message.Substring(0,[Math]::Min(200,$_.Message.Length))}} |
    Format-Table -AutoSize -Wrap

# ----------------------------------------------------------------------
# 5. CURRENT PROCESS STATE (if still running in the bad state — run NOW)
# ----------------------------------------------------------------------
Write-Host "`n[5] CURRENT PROCESS STATE (run while still degraded!)" -ForegroundColor Yellow
$proc = Get-Process TestAgentGrpc -ErrorAction SilentlyContinue
if ($proc) {
    [PSCustomObject]@{
        PID            = $proc.Id
        Threads        = $proc.Threads.Count
        Handles        = $proc.HandleCount
        WorkingSetMB   = [math]::Round($proc.WorkingSet64/1MB,1)
        PrivateMemMB   = [math]::Round($proc.PrivateMemorySize64/1MB,1)
        GdiObjects     = $proc.HandleCount
        TotalCPUsec    = [math]::Round($proc.TotalProcessorTime.TotalSeconds,1)
        StartTime      = $proc.StartTime
        Responding     = $proc.Responding
    } | Format-List

    # Thread states — a pile of waiting threads suggests deadlock/exhaustion
    Write-Host "Thread wait reasons (top states):"
    $proc.Threads | Group-Object -Property WaitReason |
        Sort-Object Count -Descending |
        Select-Object Count, Name | Format-Table -AutoSize
} else {
    Write-Host "  TestAgentGrpc process not currently running" -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# 6. PORT STATE
# ----------------------------------------------------------------------
Write-Host "`n[6] PORT $Port STATE" -ForegroundColor Yellow
$conns = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
if ($conns) {
    $conns | Group-Object State |
        Select-Object Count, Name | Format-Table -AutoSize
    Write-Host "Listener present: $([bool]($conns | Where-Object State -eq 'Listen'))"
    Write-Host "Established connections: $(($conns | Where-Object State -eq 'Established').Count)"
    Write-Host "TIME_WAIT / CLOSE_WAIT: $(($conns | Where-Object State -in 'TimeWait','CloseWait').Count)"
} else {
    Write-Host "  No connections on port $Port" -ForegroundColor Gray
}

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host " Diagnostic complete. Look for:" -ForegroundColor Cyan
Write-Host "  - An exception in [2]/[3] right before the incident" -ForegroundColor Gray
Write-Host "  - The 'final words' in [1b] (last outbound call = hang)" -ForegroundColor Gray
Write-Host "  - High thread/handle counts in [5] (exhaustion/leak)" -ForegroundColor Gray
Write-Host "  - Many CLOSE_WAIT in [6] (socket leak)" -ForegroundColor Gray
Write-Host "================================================================" -ForegroundColor Cyan
