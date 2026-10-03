<#
.SYNOPSIS
    Configure restart/recovery for the TestAgent scheduled task.

.DESCRIPTION
    Replaces Configure-AgentRecovery.ps1 (archived), which configured Windows SERVICE
    recovery via sc.exe. The fleet does not run the agent as a service - it runs under
    the scheduled task named by -TaskName (fleet-inventory.json: launchMode=ScheduledTask,
    taskName="TestAgentGrpc Interactive"). The old script therefore targeted a service
    that does not exist on any node and silently did nothing.

    Dry run by default, matching deploy\Invoke-NodeCleanup.ps1. Pass -Apply to change
    anything. -WhatIf is also honoured.

    Settings applied:
      RestartCount            how many times the scheduler restarts the task after a failure
      RestartInterval         how long it waits between those restarts
      ExecutionTimeLimit      0 = unlimited, so a week-long run is not killed by the scheduler
      MultipleInstances       IgnoreNew, so a restart cannot produce two live agents
      DisallowStartIfOnBatteries / StopIfGoingOnBatteries disabled, so a laptop node keeps running

.PARAMETER ComputerName
    Remote nodes to configure. Omit to act on the local machine. Requires WinRM for remote use.

.PARAMETER Apply
    Perform changes. Without it the script only reports the planned change.

.EXAMPLE
    .\Configure-AgentTaskRecovery.ps1
    Dry run against the local machine.

.EXAMPLE
    .\Configure-AgentTaskRecovery.ps1 -ComputerName JVGR1,WARMGR
    Dry run against two nodes.

.EXAMPLE
    .\Configure-AgentTaskRecovery.ps1 -ComputerName JVGR1 -Apply
    Apply to one node.

.NOTES
    Target framework : .NET 10 (LTS) - runtime 10.0.12 or newer required
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 2.0  |  October 2026
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string[]] $ComputerName,
    [string]   $TaskName          = 'TestAgentGrpc Interactive',
    [ValidateRange(1, 999)]
    [int]      $RestartCount      = 999,
    [ValidateRange(1, 1440)]
    [int]      $RestartIntervalMinutes = 1,
    [switch]   $NoExecutionTimeLimit = $true,
    [switch]   $Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Ok   ($m) { Write-Host "  [ OK ] $m"   -ForegroundColor Green  }
function Write-Warn ($m) { Write-Host "  [WARN] $m"   -ForegroundColor Yellow }
function Write-Err  ($m) { Write-Host "  [FAIL] $m"   -ForegroundColor Red    }
function Write-Note ($m) { Write-Host "  $m"          -ForegroundColor Gray   }
function Write-Plan ($m) { Write-Host "  [PLAN] $m"   -ForegroundColor Cyan   }

$targets = if ($ComputerName -and $ComputerName.Count) { $ComputerName } else { @($env:COMPUTERNAME) }
$restartInterval = New-TimeSpan -Minutes $RestartIntervalMinutes

Write-Host ''
Write-Host '  ========================================================' -ForegroundColor Green
Write-Host '   TestAgent Scheduled-Task Recovery'                       -ForegroundColor Green
Write-Host ("   Task: {0}" -f $TaskName)                                -ForegroundColor Green
Write-Host ("   Mode: {0}" -f $(if ($Apply) { 'APPLY' } else { 'DRY RUN (use -Apply to change)' })) -ForegroundColor Green
Write-Host '  ========================================================' -ForegroundColor Green
Write-Host ''

$failures = 0

foreach ($node in $targets) {
    Write-Host ("--- {0} ---" -f $node) -ForegroundColor White
    $session = $null
    try {
        $isLocal = ($node -eq $env:COMPUTERNAME -or $node -eq '.' -or $node -eq 'localhost')
        if (-not $isLocal) {
            $session = New-CimSession -ComputerName $node -OperationTimeoutSec 30 -ErrorAction Stop
        }

        $getArgs = @{ TaskName = $TaskName; ErrorAction = 'Stop' }
        if ($session) { $getArgs['CimSession'] = $session }

        $task = $null
        try { $task = Get-ScheduledTask @getArgs }
        catch {
            Write-Err "Task not found: '$TaskName'. Nothing to configure."
            Write-Note "Available TestAgent tasks:"
            $listArgs = @{ ErrorAction = 'SilentlyContinue' }
            if ($session) { $listArgs['CimSession'] = $session }
            Get-ScheduledTask @listArgs | Where-Object { $_.TaskName -match 'TestAgent' } |
                ForEach-Object { Write-Note ("    {0}{1}" -f $_.TaskPath, $_.TaskName) }
            $failures++
            continue
        }

        $s = $task.Settings
        Write-Note ("current: RestartCount={0} RestartInterval={1} ExecutionTimeLimit={2} MultipleInstances={3}" -f `
            $s.RestartCount, $s.RestartInterval, $s.ExecutionTimeLimit, $s.MultipleInstances)

        $wantInterval = [System.Xml.XmlConvert]::ToString($restartInterval)
        $wantLimit    = if ($NoExecutionTimeLimit) { 'PT0S' } else { $s.ExecutionTimeLimit }

        $changes = New-Object System.Collections.Generic.List[string]
        if ("$($s.RestartCount)"       -ne "$RestartCount")  { [void]$changes.Add("RestartCount $($s.RestartCount) -> $RestartCount") }
        if ("$($s.RestartInterval)"    -ne "$wantInterval")  { [void]$changes.Add("RestartInterval $($s.RestartInterval) -> $wantInterval") }
        if ($NoExecutionTimeLimit -and "$($s.ExecutionTimeLimit)" -ne 'PT0S') { [void]$changes.Add("ExecutionTimeLimit $($s.ExecutionTimeLimit) -> PT0S (unlimited)") }
        if ("$($s.MultipleInstances)"  -ne 'IgnoreNew')      { [void]$changes.Add("MultipleInstances $($s.MultipleInstances) -> IgnoreNew") }
        if ($s.DisallowStartIfOnBatteries) { [void]$changes.Add('DisallowStartIfOnBatteries -> disabled') }
        if ($s.StopIfGoingOnBatteries)     { [void]$changes.Add('StopIfGoingOnBatteries -> disabled') }

        if (-not $changes.Count) { Write-Ok 'Already configured - no change needed.'; continue }
        foreach ($chg in $changes) { Write-Plan $chg }

        if (-not $Apply) { Write-Note 'Dry run - nothing written. Re-run with -Apply.'; continue }

        if ($PSCmdlet.ShouldProcess($node, "Update scheduled task '$TaskName' recovery settings")) {
            $s.RestartCount                = $RestartCount
            $s.RestartInterval             = $wantInterval
            if ($NoExecutionTimeLimit) { $s.ExecutionTimeLimit = 'PT0S' }
            $s.MultipleInstances           = 'IgnoreNew'
            $s.DisallowStartIfOnBatteries  = $false
            $s.StopIfGoingOnBatteries      = $false

            $setArgs = @{ TaskName = $TaskName; Settings = $s; ErrorAction = 'Stop' }
            if ($session) { $setArgs['CimSession'] = $session }
            Set-ScheduledTask @setArgs | Out-Null

            $after = (Get-ScheduledTask @getArgs).Settings
            Write-Ok ("applied: RestartCount={0} RestartInterval={1} ExecutionTimeLimit={2} MultipleInstances={3}" -f `
                $after.RestartCount, $after.RestartInterval, $after.ExecutionTimeLimit, $after.MultipleInstances)
        }
    } catch {
        Write-Err $_.Exception.Message
        $failures++
    } finally {
        if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue }
    }
    Write-Host ''
}

if ($failures -gt 0) { Write-Warn ("{0} node(s) failed." -f $failures); exit 1 }
exit 0
