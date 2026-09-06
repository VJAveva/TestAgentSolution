<#
.SYNOPSIS
    Ships the Windows Update detection release to the agent fleet and the controller.

.DESCRIPTION
    Purpose-built for this rollout because deploy-agent.bat cannot be used as-is here:

      * deploy-agent.bat robocopy /MIR's the publish folder INCLUDING appsettings.json.
        The deployed nodes carry node-local settings (ControllerAddress=http://jvgr22:5100,
        per-node AgentName/AgentEndpoint) that a wholesale overwrite would destroy.
      * It does not stop the "TestAgentGrpc Interactive" scheduled task, so the copy hits
        locked binaries on a running node and half-deploys.

    This script instead, per node:
      1. STOP    the scheduled task and wait for the process to exit.
      2. BACKUP  the remote folder to a local timestamped snapshot.
      3. COPY    binaries with /MIR but EXCLUDING appsettings.json (config is never clobbered).
      4. PATCH   the remote appsettings.json surgically - merges the new "WindowsUpdate"
                 section and repairs a wrong AgentName/AgentEndpoint, preserving everything else.
      5. START   the scheduled task and confirm it is running.

    Use -DryRun first: it performs every read and reports the exact planned mutations
    without stopping a task or writing a byte.

.PARAMETER Agents
    Agent node names. Defaults to the nine-node lab fleet.

.PARAMETER ControllerNode
    Controller machine. Deployed only when -IncludeController is set.

.PARAMETER IncludeController
    Also deploy \publish\controller to \\<ControllerNode>\C$\TestControllerService.
    The controller is a WPF desktop app: it MUST be closed first (the script verifies).

.PARAMETER SkipAgents
    Deploy only the controller.

.PARAMETER DryRun
    Report planned actions only. No task is stopped, no file is written.

.EXAMPLE
    .\Deploy-UpdateDetectionRelease.ps1 -DryRun
.EXAMPLE
    .\Deploy-UpdateDetectionRelease.ps1 -IncludeController
.EXAMPLE
    .\Deploy-UpdateDetectionRelease.ps1 -Agents jvgr1 -DryRun
#>
[CmdletBinding()]
param(
    [string[]]$Agents = @('jvgr1','jvkpri','jvkbak','jvhist','jvgr2','warmgr','warmbak','warmhist','warmpri'),
    [string]$ControllerNode = 'jvgr22',
    [switch]$IncludeController,
    [switch]$SkipAgents,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $PSScriptRoot
$agentSrc   = Join-Path $repoRoot 'publish\agent'
$ctrlSrc    = Join-Path $repoRoot 'publish\controller'
$backupRoot = Join-Path $repoRoot 'publish\_backups'
$stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskName   = 'TestAgentGrpc Interactive'

function Step($m) { Write-Host "  [ .. ] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

# The config block this release adds. Mirrors TestAgentGrpc/appsettings.json.
$windowsUpdateDefaults = [ordered]@{
    'Enabled'                 = $true
    'PollIntervalSeconds'     = 300
    'CoalescingWindowSeconds' = 30
    'MaxCoalescingSeconds'    = 300
    'SnapshotIntervalSeconds' = 3600
    'StartupDelaySeconds'     = 20
    'ScanTimeoutSeconds'      = 180
    'ScanPendingUpdates'      = $true
    'WatchEventLog'           = $true
    'MaxReportedItems'        = 30
}

if (-not (Test-Path $agentSrc)) { throw "Agent publish output not found: $agentSrc" }
if ($IncludeController -and -not (Test-Path $ctrlSrc)) { throw "Controller publish output not found: $ctrlSrc" }

$results = [System.Collections.Generic.List[object]]::new()

# -- Agents --------------------------------------------------------------------
function Deploy-Agent([string]$node) {
    $remote  = "\\$node\C`$\TestAgentService"
    $cfgPath = Join-Path $remote 'appsettings.json'
    $status  = [ordered]@{ Node = $node; Stopped = ''; Backup = ''; Copy = ''; Config = ''; Started = '' }

    Write-Host "`n=== $node ===" -ForegroundColor White

    if (-not (Test-Path $remote)) { Fail "share unreachable: $remote"; $status.Copy = 'UNREACHABLE'; return [pscustomobject]$status }

    # Read config first so we can report the planned surgical edits even in dry-run.
    $cfg = $null
    if (Test-Path $cfgPath) {
        try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json }
        catch { Fail "appsettings.json unparseable - skipping node"; $status.Config = 'PARSE-ERR'; return [pscustomobject]$status }
    } else { Fail "appsettings.json missing - skipping node"; $status.Config = 'MISSING'; return [pscustomobject]$status }

    $edits = @()
    if (-not ($cfg.PSObject.Properties.Name -contains 'WindowsUpdate')) { $edits += 'add WindowsUpdate section' }
    # warmpri currently advertises itself as warmbak; that collides with the real warmbak.
    if ($cfg.AgentSettings.AgentName -and $cfg.AgentSettings.AgentName -ne $node) {
        $edits += "AgentName '$($cfg.AgentSettings.AgentName)' -> '$node'"
    }
    $wantEndpoint = "http://${node}:$($cfg.AgentSettings.GrpcPort)"
    if ($cfg.AgentSettings.AgentEndpoint -and $cfg.AgentSettings.AgentEndpoint -ne $wantEndpoint) {
        $edits += "AgentEndpoint '$($cfg.AgentSettings.AgentEndpoint)' -> '$wantEndpoint'"
    }
    $status.Config = if ($edits) { $edits -join '; ' } else { 'no change needed' }

    if ($DryRun) {
        Step "would stop task '$taskName'"
        Step "would back up $remote"
        Step "would copy $((Get-ChildItem $agentSrc -Recurse -File).Count) files (excluding appsettings.json)"
        Step "config: $($status.Config)"
        Step "would start task '$taskName'"
        $status.Stopped = $status.Backup = $status.Copy = $status.Started = 'DRY-RUN'
        return [pscustomobject]$status
    }

    # 1. Stop
    Step "stopping '$taskName'"
    schtasks /End /S $node /TN $taskName 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    # The binary must be unlocked or the copy half-lands.
    $probe = Join-Path $remote 'TestAgentGrpc.dll'
    $unlocked = $false
    foreach ($try in 1..10) {
        try { $fs = [IO.File]::Open($probe,'Open','ReadWrite','None'); $fs.Close(); $unlocked = $true; break }
        catch { Start-Sleep -Seconds 2 }
    }
    if (-not $unlocked) { Fail "binaries still locked after 20s - skipping node"; $status.Stopped = 'LOCKED'; return [pscustomobject]$status }
    $status.Stopped = 'yes'; Ok 'task stopped, binaries unlocked'

    # 2. Backup
    $bdir = Join-Path $backupRoot "agent-$node\$stamp"
    Step "backing up -> $bdir"
    New-Item -ItemType Directory -Path $bdir -Force | Out-Null
    robocopy $remote $bdir /E /NJH /NJS /NDL /NP /NFL /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "backup failed (robocopy $LASTEXITCODE) - NOT deploying"; $status.Backup = 'FAIL'; schtasks /Run /S $node /TN $taskName 2>&1 | Out-Null; return [pscustomobject]$status }
    $status.Backup = $stamp; Ok 'backed up'

    # 3. Copy binaries, never the config
    Step 'copying binaries (appsettings.json excluded)'
    robocopy $agentSrc $remote /MIR /XF appsettings.json /XD Logs /NJH /NJS /NDL /NP /NFL /R:2 /W:2 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "copy failed (robocopy $LASTEXITCODE)"; $status.Copy = 'FAIL'; return [pscustomobject]$status }
    $status.Copy = 'ok'; Ok 'binaries copied'

    # 4. Surgical config patch
    if ($edits) {
        Step "patching config: $($status.Config)"
        Copy-Item $cfgPath "$cfgPath.bak-$stamp" -Force
        if (-not ($cfg.PSObject.Properties.Name -contains 'WindowsUpdate')) {
            $cfg | Add-Member -NotePropertyName 'WindowsUpdate' -NotePropertyValue ([pscustomobject]$windowsUpdateDefaults)
        }
        $cfg.AgentSettings.AgentName = $node
        $cfg.AgentSettings.AgentEndpoint = $wantEndpoint
        $cfg | ConvertTo-Json -Depth 20 | Set-Content $cfgPath -Encoding UTF8
        Ok 'config patched'
    } else { Ok 'config already correct' }

    # 5. Start
    Step "starting '$taskName'"
    schtasks /Run /S $node /TN $taskName 2>&1 | Out-Null
    Start-Sleep -Seconds 4
    $q = schtasks /Query /S $node /TN $taskName /FO LIST 2>&1 | Out-String
    $status.Started = if ($q -match 'Status:\s+Running') { 'Running' } elseif ($q -match 'Status:\s+(\S+)') { $Matches[1] } else { 'UNKNOWN' }
    if ($status.Started -eq 'Running') { Ok 'agent running' } else { Warn "task status = $($status.Started)" }

    return [pscustomobject]$status
}

# -- Controller ----------------------------------------------------------------
function Deploy-Controller([string]$node) {
    $remote = "\\$node\C`$\TestControllerService"
    $status = [ordered]@{ Node = "$node (controller)"; Stopped = ''; Backup = ''; Copy = ''; Config = ''; Started = '' }

    Write-Host "`n=== $node (controller) ===" -ForegroundColor White
    if (-not (Test-Path $remote)) { Fail "share unreachable: $remote"; $status.Copy = 'UNREACHABLE'; return [pscustomobject]$status }

    # WPF desktop app - no service to stop; refuse to deploy over a running instance.
    $probe = Join-Path $remote 'TestControllerGrpc.dll'
    if (Test-Path $probe) {
        try { $fs = [IO.File]::Open($probe,'Open','ReadWrite','None'); $fs.Close(); $status.Stopped = 'was-stopped' }
        catch { Fail 'controller is RUNNING - close it on jvgr22 first'; $status.Stopped = 'RUNNING-ABORT'; return [pscustomobject]$status }
    }

    if ($DryRun) {
        Step "would back up $remote"
        Step "would copy $((Get-ChildItem $ctrlSrc -Recurse -File).Count) files (excluding appsettings.json)"
        Step 'config: would merge Agents list additions if missing'
        $status.Backup = $status.Copy = $status.Config = 'DRY-RUN'
        return [pscustomobject]$status
    }

    $bdir = Join-Path $backupRoot "controller-$node\$stamp"
    Step "backing up -> $bdir"
    New-Item -ItemType Directory -Path $bdir -Force | Out-Null
    robocopy $remote $bdir /E /NJH /NJS /NDL /NP /NFL /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "backup failed - NOT deploying"; $status.Backup = 'FAIL'; return [pscustomobject]$status }
    $status.Backup = $stamp; Ok 'backed up'

    Step 'copying binaries (appsettings.json excluded)'
    robocopy $ctrlSrc $remote /MIR /XF appsettings.json /XD Logs /NJH /NJS /NDL /NP /NFL /R:2 /W:2 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "copy failed (robocopy $LASTEXITCODE)"; $status.Copy = 'FAIL'; return [pscustomobject]$status }
    $status.Copy = 'ok'; Ok 'binaries copied'

    # Merge any missing agents into the deployed Agents list; leave everything else alone.
    $cfgPath = Join-Path $remote 'appsettings.json'
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $have = @($cfg.Agents | ForEach-Object { $_.Name.ToUpperInvariant() })
        $missing = @($Agents | Where-Object { $have -notcontains $_.ToUpperInvariant() })
        if ($missing.Count -gt 0) {
            Copy-Item $cfgPath "$cfgPath.bak-$stamp" -Force
            $list = [System.Collections.Generic.List[object]]::new()
            $cfg.Agents | ForEach-Object { $list.Add($_) }
            foreach ($m in $missing) {
                $u = $m.ToUpperInvariant()
                $list.Add([pscustomobject]@{ Name = $u; Address = "http://${u}:5200" })
            }
            $cfg.Agents = $list.ToArray()
            $cfg | ConvertTo-Json -Depth 20 | Set-Content $cfgPath -Encoding UTF8
            $status.Config = "added: $($missing -join ', ')"
            Ok $status.Config
        } else { $status.Config = 'all agents present'; Ok $status.Config }
    } else { $status.Config = 'no appsettings.json'; Warn $status.Config }

    Warn 'controller is a desktop app - start it manually on jvgr22'
    return [pscustomobject]$status
}

# -- Run -----------------------------------------------------------------------
Write-Host "TestAgentSolution - Windows Update detection release" -ForegroundColor White
Write-Host "Mode      : $(if($DryRun){'DRY RUN (no changes)'}else{'LIVE DEPLOY'})" -ForegroundColor $(if($DryRun){'Yellow'}else{'Red'})
Write-Host "Agent src : $agentSrc"
if ($IncludeController) { Write-Host "Ctrl src  : $ctrlSrc" }
Write-Host "Backups   : $backupRoot"

if (-not $SkipAgents) { foreach ($n in $Agents) { $results.Add((Deploy-Agent $n)) } }
if ($IncludeController) { $results.Add((Deploy-Controller $ControllerNode)) }

Write-Host "`n===================== SUMMARY =====================" -ForegroundColor White
$results | Format-Table -AutoSize
if (-not $DryRun) { Write-Host "Rollback: restore from $backupRoot\<component>-<node>\$stamp" -ForegroundColor DarkGray }
