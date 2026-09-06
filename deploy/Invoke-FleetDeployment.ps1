<#
.SYNOPSIS
    Inventory-driven deployment for the TestAgentSolution fleet: patches binaries,
    surgically updates config, and emits an HTML + JSON activity report.

.DESCRIPTION
    Replaces ad-hoc use of deploy-agent.bat for multi-node rollouts. That script
    robocopy /MIR's the publish folder INCLUDING appsettings.json (which destroys
    node-local values such as ControllerAddress) and never stops the agent's
    scheduled task, so the copy lands on locked binaries and half-deploys.

    Per node this script performs:

      1. PRECHECK  share reachable, publish source present, config parseable.
      2. STOP      scheduled task (agents) / verify closed (controller), then wait
                   until the probe binary is genuinely unlocked before copying.
      3. BACKUP    full remote folder -> timestamped local snapshot.
      4. COPY      robocopy /MIR excluding every file in 'preserveFiles' and every
                   folder in 'preserveFolders' - config is NEVER clobbered.
      5. PATCH     surgical JSON edit driven by the inventory:
                     merge -> add key only when absent (safe to re-run)
                     set   -> force value (deployer-owned keys)
                   Tokens: $NODE $GRPCPORT $CONTROLLER $CONTROLLERPORT $WEBAPIPORT
      6. START     scheduled task, then confirm Running + TCP port listening.
      7. REPORT    console table + HTML + JSON, recording every mutation.

    Every node is isolated: a failure is recorded and the run continues.
    Nothing is written in -DryRun, which still performs all reads so the planned
    mutations are reported accurately.

.PARAMETER InventoryPath
    Path to a .json or flat .txt inventory. Default: deploy/fleet-inventory.json.

.PARAMETER PatchTemplate
    JSON inventory supplying defaults + config patch when -InventoryPath is a flat
    file. Default: deploy/fleet-inventory.json.

.PARAMETER Component
    all (default) | agents | controller.

.PARAMETER Only
    Restrict to a subset of node names from the inventory.

.PARAMETER DryRun
    Report planned actions; make no changes.

.PARAMETER Rollback
    Restore from backups instead of deploying. Requires -BackupStamp.

.PARAMETER BackupStamp
    Backup folder name (timestamp) to restore, e.g. 20260906-143800.

.PARAMETER ReportDir
    Where reports are written. Default: publish/_reports.

.PARAMETER SkipVerify
    Skip the post-deploy port/task verification.

.EXAMPLE
    .\Invoke-FleetDeployment.ps1 -DryRun
.EXAMPLE
    .\Invoke-FleetDeployment.ps1 -Component agents -Only jvgr1
.EXAMPLE
    .\Invoke-FleetDeployment.ps1 -InventoryPath .\fleet-inventory.txt
.EXAMPLE
    .\Invoke-FleetDeployment.ps1 -Rollback -BackupStamp 20260906-143800
#>
[CmdletBinding()]
param(
    [string]$InventoryPath,
    [string]$PatchTemplate,
    [ValidateSet('all','agents','controller')]
    [string]$Component = 'all',
    [string[]]$Only,
    [switch]$DryRun,
    [switch]$Rollback,
    [string]$BackupStamp,
    [string]$ReportDir,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $PSScriptRoot
$stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
$runStart   = Get-Date
$backupRoot = Join-Path $repoRoot 'publish\_backups'
if (-not $InventoryPath)  { $InventoryPath  = Join-Path $PSScriptRoot 'fleet-inventory.json' }
if (-not $PatchTemplate)  { $PatchTemplate  = Join-Path $PSScriptRoot 'fleet-inventory.json' }
if (-not $ReportDir)      { $ReportDir      = Join-Path $repoRoot 'publish\_reports' }

if ($Rollback -and -not $BackupStamp) { throw "-Rollback requires -BackupStamp (e.g. 20260906-143800)." }

# -- console helpers -----------------------------------------------------------
function Write-Head($m) { Write-Host "`n$m" -ForegroundColor White }
function Write-Node($m) { Write-Host "`n=== $m ===" -ForegroundColor White }
function Step($m) { Write-Host "  [ .. ] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

# -- inventory loading ---------------------------------------------------------
function Read-Inventory {
    param([string]$Path, [string]$Template)

    if (-not (Test-Path $Path)) { throw "Inventory not found: $Path" }
    $isJson = [IO.Path]::GetExtension($Path).ToLowerInvariant() -eq '.json'

    if ($isJson) {
        try { return Get-Content $Path -Raw | ConvertFrom-Json }
        catch { throw "Inventory '$Path' is not valid JSON: $($_.Exception.Message)" }
    }

    # Flat file: node list only; defaults + patch come from the JSON template.
    if (-not (Test-Path $Template)) { throw "Flat inventory needs -PatchTemplate; '$Template' not found." }
    $inv = Get-Content $Template -Raw | ConvertFrom-Json

    $agents = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in Get-Content $Path) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }

        if ($line -match '^controller\s*:\s*(.+)$') {
            $inv.controller.node = $Matches[1].Trim()
            continue
        }
        # <node>[:<grpcPort>]
        $parts = $line -split ':', 2
        $entry = [pscustomobject]@{ node = $parts[0].Trim() }
        if ($parts.Count -eq 2 -and $parts[1].Trim() -match '^\d+$') {
            $entry | Add-Member -NotePropertyName grpcPort -NotePropertyValue ([int]$parts[1].Trim())
        }
        $agents.Add($entry)
    }
    if ($agents.Count -eq 0) { throw "Flat inventory '$Path' listed no agent nodes." }
    $inv.agents = $agents.ToArray()
    return $inv
}

function Get-Prop { param($Obj, [string]$Name, $Default = $null)
    if ($null -ne $Obj -and $Obj.PSObject.Properties.Name -contains $Name -and $null -ne $Obj.$Name) { return $Obj.$Name }
    return $Default
}

# -- config patching -----------------------------------------------------------
function Expand-Token { param([string]$Value, [hashtable]$Tokens)
    # Longest first: $CONTROLLER is a prefix of $CONTROLLERPORT and would otherwise eat it.
    foreach ($k in ($Tokens.Keys | Sort-Object -Property Length -Descending)) {
        $Value = $Value -replace [regex]::Escape($k), $Tokens[$k]
    }
    return $Value
}

# Adds a property only when absent, recursing into nested objects so an existing
# section gains new keys without losing operator-tuned ones.
function Merge-Missing {
    param($Target, $Source, [string]$Prefix, [System.Collections.Generic.List[string]]$Changes)

    foreach ($p in $Source.PSObject.Properties) {
        $path = if ($Prefix) { "$Prefix.$($p.Name)" } else { $p.Name }
        if ($Target.PSObject.Properties.Name -notcontains $p.Name) {
            $Target | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force
            $Changes.Add("+ $path")
        }
        elseif ($p.Value -is [psobject] -and $p.Value.PSObject.Properties.Count -gt 0 -and $Target.$($p.Name) -is [psobject]) {
            Merge-Missing -Target $Target.$($p.Name) -Source $p.Value -Prefix $path -Changes $Changes
        }
    }
}

# Forces a dotted path to a value, creating intermediate objects as needed.
function Set-ConfigPath {
    param($Root, [string]$Path, $Value, [System.Collections.Generic.List[string]]$Changes)

    $segments = $Path -split '\.'
    $cursor = $Root
    for ($i = 0; $i -lt $segments.Count - 1; $i++) {
        $seg = $segments[$i]
        if ($cursor.PSObject.Properties.Name -notcontains $seg -or $null -eq $cursor.$seg) {
            $cursor | Add-Member -NotePropertyName $seg -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        $cursor = $cursor.$seg
    }
    $leaf = $segments[-1]
    $current = if ($cursor.PSObject.Properties.Name -contains $leaf) { $cursor.$leaf } else { $null }
    if ("$current" -ne "$Value") {
        $cursor | Add-Member -NotePropertyName $leaf -NotePropertyValue $Value -Force
        $Changes.Add("~ $Path : '$current' -> '$Value'")
    }
}

function Invoke-ConfigPatch {
    param([string]$ConfigPath, $Patch, [hashtable]$Tokens, [switch]$WhatIfOnly)

    $changes = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path $ConfigPath)) { return @{ Changes = @('config file missing'); Applied = $false; Error = 'MISSING' } }

    try { $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json }
    catch { return @{ Changes = @("unparseable: $($_.Exception.Message)"); Applied = $false; Error = 'PARSE' } }

    $merge = Get-Prop $Patch 'merge'
    if ($merge) { Merge-Missing -Target $cfg -Source $merge -Prefix '' -Changes $changes }

    $set = Get-Prop $Patch 'set'
    if ($set) {
        foreach ($p in $set.PSObject.Properties) {
            $val = if ($p.Value -is [string]) { Expand-Token -Value $p.Value -Tokens $Tokens } else { $p.Value }
            Set-ConfigPath -Root $cfg -Path $p.Name -Value $val -Changes $changes
        }
    }

    if ($changes.Count -eq 0) { return @{ Changes = @(); Applied = $false; Error = $null } }
    if ($WhatIfOnly)          { return @{ Changes = $changes; Applied = $false; Error = $null } }

    Copy-Item $ConfigPath "$ConfigPath.bak-$stamp" -Force
    $cfg | ConvertTo-Json -Depth 30 | Set-Content $ConfigPath -Encoding UTF8
    return @{ Changes = $changes; Applied = $true; Error = $null }
}

# Controller-only: ensure every inventory agent appears in the Agents array.
function Merge-AgentsList {
    param([string]$ConfigPath, [string[]]$Nodes, [int]$Port, [switch]$WhatIfOnly)

    $changes = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path $ConfigPath)) { return @{ Changes = @('config file missing'); Applied = $false } }

    $cfg  = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    $have = @()
    if ($cfg.PSObject.Properties.Name -contains 'Agents' -and $cfg.Agents) {
        $have = @($cfg.Agents | ForEach-Object { "$($_.Name)".ToUpperInvariant() })
    }
    $missing = @($Nodes | Where-Object { $have -notcontains $_.ToUpperInvariant() })
    if ($missing.Count -eq 0) { return @{ Changes = @(); Applied = $false } }

    foreach ($m in $missing) { $changes.Add("+ Agents[] $($m.ToUpperInvariant())") }
    if ($WhatIfOnly) { return @{ Changes = $changes; Applied = $false } }

    $list = [System.Collections.Generic.List[object]]::new()
    if ($cfg.Agents) { $cfg.Agents | ForEach-Object { $list.Add($_) } }
    foreach ($m in $missing) {
        $u = $m.ToUpperInvariant()
        $list.Add([pscustomobject]@{ Name = $u; Address = "http://${u}:$Port" })
    }
    $cfg | Add-Member -NotePropertyName 'Agents' -NotePropertyValue $list.ToArray() -Force
    Copy-Item $ConfigPath "$ConfigPath.bak-$stamp" -Force
    $cfg | ConvertTo-Json -Depth 30 | Set-Content $ConfigPath -Encoding UTF8
    return @{ Changes = $changes; Applied = $true }
}

# -- process / task control ----------------------------------------------------
function Wait-Unlocked {
    param([string]$ProbePath, [int]$TimeoutSeconds = 30)
    if (-not (Test-Path $ProbePath)) { return $true }   # first-time deploy
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { $fs = [IO.File]::Open($ProbePath,'Open','ReadWrite','None'); $fs.Close(); return $true }
        catch { Start-Sleep -Seconds 2 }
    }
    return $false
}

function Get-TaskStatus {
    param([string]$Node, [string]$TaskName)
    $out = schtasks /Query /S $Node /TN $TaskName /FO LIST 2>&1 | Out-String
    if ($out -match 'Status:\s+(\S+)') { return $Matches[1] }
    return 'UNKNOWN'
}

function Invoke-Robocopy {
    param([string]$Source, [string]$Destination, [string[]]$ExcludeFiles, [string[]]$ExcludeDirs, [switch]$Mirror)
    $args = @($Source, $Destination)
    if ($Mirror) { $args += '/MIR' } else { $args += '/E' }
    if ($ExcludeFiles) { $args += '/XF'; $args += $ExcludeFiles }
    if ($ExcludeDirs)  { $args += '/XD'; $args += $ExcludeDirs }
    $args += @('/NJH','/NJS','/NDL','/NP','/NFL','/R:2','/W:2')
    & robocopy @args | Out-Null
    return $LASTEXITCODE
}

# -- per-node deployment -------------------------------------------------------
function Deploy-Node {
    param($Spec, $Patch, [hashtable]$Tokens, [string]$Kind)

    $node   = $Spec.Node
    $remote = "\\$node\$($Spec.SharePath)"
    $source = Join-Path $repoRoot $Spec.PublishDir
    $cfg    = Join-Path $remote 'appsettings.json'

    $r = [ordered]@{
        Node = $node; Kind = $Kind; Result = 'PENDING'; Stopped = ''; Backup = ''
        FilesCopied = 0; ConfigChanges = @(); Started = ''; Port = ''; Error = ''
        StartedAt = (Get-Date).ToString('HH:mm:ss')
    }
    Write-Node "$node ($Kind)"

    try {
        # 1. Precheck
        if (-not (Test-Path $source)) { throw "publish source missing: $source" }
        if (-not (Test-Path $remote)) { throw "share unreachable: $remote" }

        $probe = Join-Path $remote $Spec.LockProbe
        $srcCount = (Get-ChildItem $source -Recurse -File).Count

        # Report planned config edits even in dry-run.
        $planned = Invoke-ConfigPatch -ConfigPath $cfg -Patch $Patch -Tokens $Tokens -WhatIfOnly
        if ($planned.Error) { throw "appsettings.json $($planned.Error)" }
        $r.ConfigChanges = @($planned.Changes)

        if ($DryRun) {
            Step "would stop $($Spec.LaunchMode)"
            Step "would back up $remote"
            Step "would copy $srcCount files (preserving: $($Spec.PreserveFiles -join ', '))"
            if ($r.ConfigChanges) { $r.ConfigChanges | ForEach-Object { Step "config $_" } } else { Step 'config: no change needed' }
            Step 'would start and verify'
            $r.Result = 'DRY-RUN'; $r.FilesCopied = $srcCount
            return [pscustomobject]$r
        }

        # 2. Stop
        if ($Spec.LaunchMode -eq 'ScheduledTask') {
            Step "stopping task '$($Spec.TaskName)'"
            schtasks /End /S $node /TN $Spec.TaskName 2>&1 | Out-Null
            Start-Sleep -Seconds 2
            if (-not (Wait-Unlocked -ProbePath $probe)) { throw 'binaries still locked after 30s' }
            $r.Stopped = 'task stopped'; Ok 'task stopped, binaries unlocked'
        }
        else {
            if (-not (Wait-Unlocked -ProbePath $probe -TimeoutSeconds 1)) {
                throw "$($Spec.ProcessName) is RUNNING on $node - close it first (manual-launch component)"
            }
            $r.Stopped = 'was closed'; Ok 'not running - safe to deploy'
        }

        # 3. Backup
        $bdir = Join-Path $backupRoot "$Kind-$node\$stamp"
        Step "backing up -> $bdir"
        New-Item -ItemType Directory -Path $bdir -Force | Out-Null
        if ((Invoke-Robocopy -Source $remote -Destination $bdir) -ge 8) { throw 'backup failed - nothing deployed' }
        $r.Backup = $stamp; Ok 'backed up'

        # 4. Copy (config preserved)
        Step "copying $srcCount files"
        $rc = Invoke-Robocopy -Source $source -Destination $remote -ExcludeFiles $Spec.PreserveFiles -ExcludeDirs $Spec.PreserveFolders -Mirror
        if ($rc -ge 8) { throw "robocopy failed with exit code $rc" }
        $r.FilesCopied = $srcCount; Ok 'binaries copied'

        # 5. Patch
        if ($r.ConfigChanges.Count -gt 0) {
            Step 'patching config'
            $applied = Invoke-ConfigPatch -ConfigPath $cfg -Patch $Patch -Tokens $Tokens
            $r.ConfigChanges = @($applied.Changes)
            $r.ConfigChanges | ForEach-Object { Ok "config $_" }
        } else { Ok 'config already correct' }

        if ($Kind -eq 'controller' -and (Get-Prop $Patch 'mergeAgentsList' $false)) {
            $merged = Merge-AgentsList -ConfigPath $cfg -Nodes $script:AgentNodeNames -Port $script:AgentPort
            if ($merged.Changes) { $r.ConfigChanges += $merged.Changes; $merged.Changes | ForEach-Object { Ok "config $_" } }
        }

        # 6. Start + verify
        if ($Spec.LaunchMode -eq 'ScheduledTask') {
            Step "starting task '$($Spec.TaskName)'"
            schtasks /Run /S $node /TN $Spec.TaskName 2>&1 | Out-Null
            Start-Sleep -Seconds 4
            $r.Started = Get-TaskStatus -Node $node -TaskName $Spec.TaskName
            if ($r.Started -eq 'Running') { Ok 'task running' } else { Warn "task status = $($r.Started)" }
        } else {
            $r.Started = 'manual-start-required'
            Warn "$($Spec.ProcessName) is manual-launch - start it on $node"
        }

        if (-not $SkipVerify -and $Spec.GrpcPort) {
            $t = Test-NetConnection -ComputerName $node -Port $Spec.GrpcPort -WarningAction SilentlyContinue
            $r.Port = if ($t.TcpTestSucceeded) { "$($Spec.GrpcPort) listening" } else { "$($Spec.GrpcPort) CLOSED" }
            if ($t.TcpTestSucceeded) { Ok $r.Port } else { Warn $r.Port }
        }

        $r.Result = if ($Spec.LaunchMode -eq 'ScheduledTask' -and $r.Started -ne 'Running') { 'WARN' }
                    elseif ($r.Port -like '*CLOSED*') { 'WARN' }
                    else { 'SUCCESS' }
    }
    catch {
        $r.Result = 'FAILED'; $r.Error = $_.Exception.Message
        Fail $r.Error
        # Never leave an agent stopped because the deploy failed.
        if ($Spec.LaunchMode -eq 'ScheduledTask' -and $r.Stopped) {
            schtasks /Run /S $node /TN $Spec.TaskName 2>&1 | Out-Null
            Warn 'attempted to restart the agent after failure'
        }
    }
    return [pscustomobject]$r
}

function Restore-Node {
    param($Spec, [string]$Kind)

    $node   = $Spec.Node
    $remote = "\\$node\$($Spec.SharePath)"
    $bdir   = Join-Path $backupRoot "$Kind-$node\$BackupStamp"
    $r = [ordered]@{ Node = $node; Kind = $Kind; Result = 'PENDING'; Stopped = ''; Backup = $BackupStamp
                     FilesCopied = 0; ConfigChanges = @(); Started = ''; Port = ''; Error = ''
                     StartedAt = (Get-Date).ToString('HH:mm:ss') }
    Write-Node "$node ($Kind) - ROLLBACK"

    try {
        if (-not (Test-Path $bdir)) { throw "backup not found: $bdir" }
        if ($DryRun) { Step "would restore $bdir -> $remote"; $r.Result = 'DRY-RUN'; return [pscustomobject]$r }

        if ($Spec.LaunchMode -eq 'ScheduledTask') {
            schtasks /End /S $node /TN $Spec.TaskName 2>&1 | Out-Null
            Start-Sleep -Seconds 2
            if (-not (Wait-Unlocked -ProbePath (Join-Path $remote $Spec.LockProbe))) { throw 'binaries locked' }
            $r.Stopped = 'task stopped'
        }
        Step "restoring $bdir"
        if ((Invoke-Robocopy -Source $bdir -Destination $remote -Mirror) -ge 8) { throw 'restore robocopy failed' }
        $r.FilesCopied = (Get-ChildItem $bdir -Recurse -File).Count
        Ok 'restored'

        if ($Spec.LaunchMode -eq 'ScheduledTask') {
            schtasks /Run /S $node /TN $Spec.TaskName 2>&1 | Out-Null
            Start-Sleep -Seconds 4
            $r.Started = Get-TaskStatus -Node $node -TaskName $Spec.TaskName
        }
        $r.Result = 'SUCCESS'
    }
    catch { $r.Result = 'FAILED'; $r.Error = $_.Exception.Message; Fail $r.Error }
    return [pscustomobject]$r
}

# -- reporting -----------------------------------------------------------------
function Write-Report {
    param($Results, [string]$Mode, [datetime]$Start, [datetime]$End)

    New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
    $jsonPath = Join-Path $ReportDir "deploy-$stamp.json"
    $htmlPath = Join-Path $ReportDir "deploy-$stamp.html"

    $summary = [ordered]@{
        Stamp = $stamp; Mode = $Mode; Inventory = $InventoryPath
        StartedUtc = $Start.ToUniversalTime().ToString('o')
        DurationSeconds = [math]::Round(($End - $Start).TotalSeconds, 1)
        Operator = "$env:USERDOMAIN\$env:USERNAME"; RunFrom = $env:COMPUTERNAME
        Total = $Results.Count
        Succeeded = @($Results | Where-Object Result -in 'SUCCESS','DRY-RUN').Count
        Warnings  = @($Results | Where-Object Result -eq 'WARN').Count
        Failed    = @($Results | Where-Object Result -eq 'FAILED').Count
        Nodes = $Results
    }
    $summary | ConvertTo-Json -Depth 12 | Set-Content $jsonPath -Encoding UTF8

    $rowHtml = foreach ($r in $Results) {
        $cls = switch ($r.Result) { 'SUCCESS' {'ok'} 'DRY-RUN' {'dry'} 'WARN' {'warn'} default {'bad'} }
        $cfg = if ($r.ConfigChanges) { ($r.ConfigChanges | ForEach-Object { [Web.HttpUtility]::HtmlEncode($_) }) -join '<br/>' } else { '<span class="dim">no change</span>' }
        $err = if ($r.Error) { '<div class="err">' + [Web.HttpUtility]::HtmlEncode($r.Error) + '</div>' } else { '' }
        @"
<tr>
  <td><b>$([Web.HttpUtility]::HtmlEncode($r.Node))</b><div class="dim">$($r.Kind)</div></td>
  <td><span class="pill $cls">$($r.Result)</span>$err</td>
  <td>$($r.Stopped)</td>
  <td>$($r.Backup)</td>
  <td class="num">$($r.FilesCopied)</td>
  <td class="cfg">$cfg</td>
  <td>$($r.Started)</td>
  <td>$($r.Port)</td>
</tr>
"@
    }

    $html = @"
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Fleet deployment $stamp</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;background:#1e1e2e;color:#cdd6f4;margin:0;padding:24px}
 h1{font-size:19px;margin:0 0 4px} h2{font-size:14px;color:#9399b2;font-weight:400;margin:0 0 18px}
 .cards{display:flex;gap:10px;margin-bottom:18px;flex-wrap:wrap}
 .card{background:#313244;border:1px solid #585b70;border-radius:6px;padding:10px 14px;min-width:110px}
 .card .n{font-size:20px;font-weight:600} .card .l{font-size:11px;color:#9399b2}
 table{border-collapse:collapse;width:100%;font-size:12px}
 th{background:#181825;text-align:left;padding:8px;border-bottom:1px solid #585b70;color:#9399b2;font-weight:600}
 td{padding:8px;border-bottom:1px solid #45475a;vertical-align:top}
 .num{text-align:right} .dim{color:#6c7086;font-size:11px} .cfg{font-family:Consolas,monospace;font-size:11px}
 .err{color:#f38ba8;font-size:11px;margin-top:4px;font-family:Consolas,monospace}
 .pill{padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}
 .ok{background:#1a3d2a;color:#a6e3a1} .warn{background:#3d2e0a;color:#f9e2af}
 .bad{background:#3d1a1a;color:#f38ba8} .dry{background:#1a3a5c;color:#89b4fa}
 footer{margin-top:18px;color:#6c7086;font-size:11px}
</style></head><body>
<h1>TestAgentSolution &mdash; fleet deployment report</h1>
<h2>$stamp &middot; mode <b>$Mode</b> &middot; $($summary.Operator) from $($summary.RunFrom) &middot; $($summary.DurationSeconds)s</h2>
<div class="cards">
  <div class="card"><div class="n">$($summary.Total)</div><div class="l">nodes</div></div>
  <div class="card"><div class="n" style="color:#a6e3a1">$($summary.Succeeded)</div><div class="l">succeeded</div></div>
  <div class="card"><div class="n" style="color:#f9e2af">$($summary.Warnings)</div><div class="l">warnings</div></div>
  <div class="card"><div class="n" style="color:#f38ba8">$($summary.Failed)</div><div class="l">failed</div></div>
</div>
<table>
<tr><th>Node</th><th>Result</th><th>Stop</th><th>Backup</th><th>Files</th><th>Config changes</th><th>Start</th><th>Port</th></tr>
$($rowHtml -join "`n")
</table>
<footer>Inventory: $([Web.HttpUtility]::HtmlEncode($InventoryPath))<br/>
Rollback: <code>Invoke-FleetDeployment.ps1 -Rollback -BackupStamp $stamp</code><br/>
Backups: $backupRoot</footer>
</body></html>
"@
    Set-Content -Path $htmlPath -Value $html -Encoding UTF8
    return @{ Json = $jsonPath; Html = $htmlPath; Summary = $summary }
}

# -- main ----------------------------------------------------------------------
Add-Type -AssemblyName System.Web

$inv  = Read-Inventory -Path $InventoryPath -Template $PatchTemplate
$mode = if ($Rollback) { "ROLLBACK -> $BackupStamp" } elseif ($DryRun) { 'DRY RUN' } else { 'LIVE DEPLOY' }

$ctrlNode = Get-Prop $inv.controller 'node'
$ctrlPort = [int](Get-Prop $inv.controller 'grpcPort' 5100)
$defaults = $inv.agentDefaults
$script:AgentPort      = [int](Get-Prop $defaults 'grpcPort' 5200)
$script:AgentNodeNames = @($inv.agents | ForEach-Object { $_.node })

Write-Head "TestAgentSolution - fleet deployment"
Write-Host "Mode      : $mode" -ForegroundColor $(if ($Rollback) {'Magenta'} elseif ($DryRun) {'Yellow'} else {'Red'})
Write-Host "Inventory : $InventoryPath"
Write-Host "Controller: $ctrlNode"
Write-Host "Backups   : $backupRoot"
Write-Host "Reports   : $ReportDir"

$targets = [System.Collections.Generic.List[object]]::new()

if ($Component -in 'all','agents') {
    foreach ($a in $inv.agents) {
        if ($Only -and $a.node -notin $Only) { continue }
        $targets.Add([pscustomobject]@{
            Kind = 'agent'
            Spec = [pscustomobject]@{
                Node = $a.node
                SharePath       = Get-Prop $a 'sharePath'   (Get-Prop $defaults 'sharePath'   'C$\TestAgentService')
                PublishDir      = Get-Prop $a 'publishDir'  (Get-Prop $defaults 'publishDir'  'publish\agent')
                GrpcPort        = [int](Get-Prop $a 'grpcPort' $script:AgentPort)
                TaskName        = Get-Prop $a 'taskName'    (Get-Prop $defaults 'taskName'    'TestAgentGrpc Interactive')
                ProcessName     = Get-Prop $a 'processName' (Get-Prop $defaults 'processName' 'TestAgentGrpc')
                LockProbe       = Get-Prop $a 'lockProbe'   (Get-Prop $defaults 'lockProbe'   'TestAgentGrpc.dll')
                LaunchMode      = Get-Prop $a 'launchMode'  (Get-Prop $defaults 'launchMode'  'ScheduledTask')
                PreserveFiles   = @(Get-Prop $inv 'preserveFiles'   @('appsettings.json'))
                PreserveFolders = @(Get-Prop $inv 'preserveFolders' @('Logs'))
            }
            Patch = $inv.agentConfigPatch
        })
    }
}

if ($Component -in 'all','controller' -and $ctrlNode -and (-not $Only -or $ctrlNode -in $Only)) {
    $c = $inv.controller
    $targets.Add([pscustomobject]@{
        Kind = 'controller'
        Spec = [pscustomobject]@{
            Node = $ctrlNode
            SharePath       = Get-Prop $c 'sharePath'  'C$\TestControllerService'
            PublishDir      = Get-Prop $c 'publishDir' 'publish\controller'
            GrpcPort        = $ctrlPort
            TaskName        = Get-Prop $c 'taskName' ''
            ProcessName     = Get-Prop $c 'processName' 'TestControllerGrpc'
            LockProbe       = Get-Prop $c 'lockProbe'  'TestControllerGrpc.dll'
            LaunchMode      = Get-Prop $c 'launchMode' 'Manual'
            PreserveFiles   = @(Get-Prop $inv 'preserveFiles'   @('appsettings.json'))
            PreserveFolders = @(Get-Prop $inv 'preserveFolders' @('Logs'))
        }
        Patch = $inv.controllerConfigPatch
    })
}

if ($targets.Count -eq 0) { throw 'No targets selected. Check -Component / -Only against the inventory.' }
Write-Host "Targets   : $($targets.Count) ($(($targets | Group-Object Kind | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '))"

$results = [System.Collections.Generic.List[object]]::new()
foreach ($t in $targets) {
    if ($Rollback) { $results.Add((Restore-Node -Spec $t.Spec -Kind $t.Kind)); continue }

    $tokens = @{
        '$NODE'           = $t.Spec.Node
        '$GRPCPORT'       = "$($t.Spec.GrpcPort)"
        '$CONTROLLER'     = "$ctrlNode"
        '$CONTROLLERPORT' = "$ctrlPort"
        '$WEBAPIPORT'     = "$(Get-Prop $inv.controller 'webApiPort' 5200)"
    }
    $results.Add((Deploy-Node -Spec $t.Spec -Patch $t.Patch -Tokens $tokens -Kind $t.Kind))
}

$report = Write-Report -Results $results -Mode $mode -Start $runStart -End (Get-Date)

Write-Head '===================== SUMMARY ====================='
$results | Format-Table Node, Kind, Result, Backup, FilesCopied, Started, Port -AutoSize
$s = $report.Summary
Write-Host "$($s.Succeeded) ok / $($s.Warnings) warn / $($s.Failed) failed  in $($s.DurationSeconds)s" -ForegroundColor $(if ($s.Failed) {'Red'} elseif ($s.Warnings) {'Yellow'} else {'Green'})
Write-Host "HTML report : $($report.Html)" -ForegroundColor Cyan
Write-Host "JSON report : $($report.Json)" -ForegroundColor Cyan
if (-not $DryRun -and -not $Rollback) {
    Write-Host "Rollback    : .\Invoke-FleetDeployment.ps1 -Rollback -BackupStamp $stamp" -ForegroundColor DarkGray
}

if ($s.Failed -gt 0) { exit 1 }
