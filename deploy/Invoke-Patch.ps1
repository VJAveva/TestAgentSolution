<#
.SYNOPSIS
    Surgical patch of the TestController controller and/or web tier on a target node.

.DESCRIPTION
    Copies ONLY the files whose CONTENT actually differs between a freshly published payload
    and the target. Because it never mirrors, target-only state (WatchList.xml, Parameters\,
    Logs\, orchestrator.db, impact-*.db, maintenance-logs\) is untouched BY CONSTRUCTION
    rather than by remembering to exclude it.

    Files that exist on BOTH sides but must never be overwritten (appsettings.json, web.config,
    appsettings.Production.json) are listed in $ProtectedFiles and are never copied.

    DEFAULT RUN IS A DRY RUN. Nothing is copied until you pass -Apply.

    Safety behaviour:
      * Refuses to patch the controller while it is running (its assemblies are locked).
      * Backs up every file it is about to overwrite, and ABORTS if the backup count
        does not match the number of files it intends to copy.
      * Verifies every copied file by SHA256 after copying.
      * Web tier: drops app_offline.htm to release IIS in-process DLL locks, then removes it.
      * Reports any appsettings keys the build has but the target lacks (never edits the target
        config - the deployed one is hand-tuned and ahead of source).

.PARAMETER Component
    controller | web | both.  Default: both.

.PARAMETER Node
    Target machine. Default: JVGR22.

.PARAMETER Apply
    Actually copy. Without this the script only reports what it WOULD do.

.PARAMETER SkipBuild
    Reuse the existing staging payload instead of running dotnet publish again.

.PARAMETER IncludeSpa
    Also compare/deploy the React bundle into wwwroot. Off by default because the SPA
    usually has not changed; the script always TELLS you whether it differs.

.PARAMETER Rollback
    Restore a previous patch backup instead of deploying.

.PARAMETER BackupStamp
    Which backup to restore. Defaults to the most recent. Implies -Rollback.

.EXAMPLE
    # 1. See what would change (safe, read-only)
    .\deploy\Invoke-Patch.ps1

.EXAMPLE
    # 2. Do it for real, once the controller is closed on the target
    .\deploy\Invoke-Patch.ps1 -Apply

.EXAMPLE
    # 3. Web tier only, and push the SPA too
    .\deploy\Invoke-Patch.ps1 -Component web -IncludeSpa -Apply

.EXAMPLE
    # 4. Undo the last patch
    .\deploy\Invoke-Patch.ps1 -Rollback -Apply

.NOTES
    Close the controller from its TRAY ICON, not Task Manager: a force kill makes
    CrashDumpHelper write a very large dump file.
#>
[CmdletBinding()]
param(
    [ValidateSet('controller', 'web', 'both')]
    [string]$Component = 'both',
    [string]$Node = 'JVGR22',
    [switch]$Apply,
    [switch]$SkipBuild,
    [switch]$IncludeSpa,
    [switch]$Rollback,
    [string]$BackupStamp,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if ($BackupStamp) { $Rollback = $true }

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }
$Stage = Join-Path $env:TEMP 'tcpatch'

# Files present on BOTH sides that must never be overwritten. Everything else that is
# target-only is safe automatically because we only copy files that exist in the payload.
$ProtectedFiles = @{
    controller = @('appsettings.json')
    web        = @('appsettings.json', 'appsettings.Production.json', 'web.config')
}

$Targets = @{
    controller = @{
        Path    = "\\$Node\C`$\TestControllerService"
        Project = 'TestControllerGrpc\TestControllerGrpc.csproj'
        Stage   = Join-Path $Stage 'controller'
        Label   = 'WPF controller'
    }
    web = @{
        Path    = "\\$Node\C`$\inetpub\TestControllerWeb"
        Project = 'TestController.WebApi\TestController.WebApi.csproj'
        Stage   = Join-Path $Stage 'webapi'
        Label   = 'Web tier (IIS)'
    }
}

# ---------------------------------------------------------------- helpers

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}

function Write-Ok   { param([string]$T) Write-Host "  [ OK ] $T" -ForegroundColor Green }
function Write-Warn { param([string]$T) Write-Host "  [WARN] $T" -ForegroundColor Yellow }
function Write-Bad  { param([string]$T) Write-Host "  [FAIL] $T" -ForegroundColor Red }
function Write-Info { param([string]$T) Write-Host "         $T" -ForegroundColor Gray }

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Test-ControllerRunning {
    param([string]$Machine)
    foreach ($port in 5100, 5200) {
        $probe = Test-NetConnection -ComputerName $Machine -Port $port -WarningAction SilentlyContinue
        if ($probe.TcpTestSucceeded) { return $true }
    }
    return $false
}

function Invoke-Publish {
    param([string]$Project, [string]$OutDir, [string]$Config)

    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

    $projPath = Join-Path $RepoRoot $Project
    Write-Info "publishing $Project ..."
    $log = & dotnet publish $projPath -c $Config -p:SkipSpaPublish=true -o $OutDir 2>&1 |
        ForEach-Object { $_.ToString() }

    $errors = @($log | Select-String -Pattern 'error CS|error MSB')
    if ($errors.Count -gt 0) {
        $errors | Select-Object -First 10 | ForEach-Object { Write-Bad $_.Line.Trim() }
        throw "Publish failed for $Project."
    }
    Write-Ok "published to $OutDir"
}

# Returns the minimal set of relative paths whose content differs.
function Get-ChangedFile {
    param(
        [string]$SourceRoot,
        [string]$TargetRoot,
        [string[]]$Protected,
        [switch]$SkipWwwroot
    )

    $protectedSet = @{}
    foreach ($p in $Protected) { $protectedSet[$p.ToLowerInvariant()] = $true }

    $changed = New-Object System.Collections.Generic.List[object]
    $srcLen = $SourceRoot.Length + 1

    foreach ($file in Get-ChildItem -Path $SourceRoot -Recurse -File) {
        $rel = $file.FullName.Substring($srcLen)

        if ($protectedSet.ContainsKey($rel.ToLowerInvariant())) { continue }
        if ($SkipWwwroot -and $rel -like 'wwwroot\*') { continue }

        $dst = Join-Path $TargetRoot $rel
        if (-not (Test-Path $dst)) {
            $changed.Add([pscustomobject]@{ Rel = $rel; Reason = 'new'; Old = 0; New = $file.Length })
            continue
        }

        $dstItem = Get-Item $dst
        if ((Get-Sha $file.FullName) -eq (Get-Sha $dst)) { continue }

        # A .NET assembly gets a fresh MVID on every compile, so an unchanged source file still
        # produces a new hash. Identical size means "recompiled", not "behaviour changed".
        $reason = 'changed'
        if ($dstItem.Length -eq $file.Length) { $reason = 'rebuilt' }

        $changed.Add([pscustomobject]@{
            Rel = $rel; Reason = $reason; Old = $dstItem.Length; New = $file.Length
        })
    }
    return $changed
}

function Backup-File {
    param([string]$TargetRoot, $Changed, [string]$Stamp)

    $backupRoot = Join-Path $TargetRoot "_patchbackup\$Stamp"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

    $expected = @($Changed | Where-Object { $_.Reason -ne 'new' }).Count
    $done = 0
    foreach ($item in $Changed) {
        $dst = Join-Path $TargetRoot $item.Rel
        if (-not (Test-Path $dst)) { continue }   # 'new' files have nothing to back up
        $bak = Join-Path $backupRoot $item.Rel
        New-Item -ItemType Directory -Path (Split-Path $bak -Parent) -Force | Out-Null
        Copy-Item -Path $dst -Destination $bak -Force
        $done++
    }

    # A backup that silently copies nothing is how a rollback point gets destroyed.
    if ($done -ne $expected) {
        throw "ABORT: backed up $done file(s) but expected $expected. Nothing has been overwritten."
    }

    Write-Ok "backed up $done file(s) to _patchbackup\$Stamp"
    return $backupRoot
}

function Copy-Verified {
    param([string]$SourceRoot, [string]$TargetRoot, $Changed)

    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($item in $Changed) {
        $src = Join-Path $SourceRoot $item.Rel
        $dst = Join-Path $TargetRoot $item.Rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }

        $copied = $false
        for ($try = 1; $try -le 12 -and -not $copied; $try++) {
            try {
                Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
                $copied = $true
            }
            catch {
                Start-Sleep -Milliseconds 500   # IIS/controller may still be releasing the lock
            }
        }
        if (-not $copied) { $failed.Add("$($item.Rel) (locked)"); continue }

        if ((Get-Sha $src) -ne (Get-Sha $dst)) { $failed.Add("$($item.Rel) (hash mismatch)") }
    }
    return $failed
}

# Flattens JSON to path=value leaves so a config comparison is provable, not eyeballed.
function Get-JsonLeaf {
    param($Node, [string]$Prefix)

    $out = New-Object System.Collections.Generic.List[string]
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Node.PSObject.Properties) {
            $out.AddRange([string[]]@(Get-JsonLeaf -Node $prop.Value -Prefix "$Prefix/$($prop.Name)"))
        }
    }
    elseif ($Node -is [Object[]]) {
        for ($i = 0; $i -lt $Node.Count; $i++) {
            $out.AddRange([string[]]@(Get-JsonLeaf -Node $Node[$i] -Prefix "$Prefix[$i]"))
        }
    }
    else {
        $out.Add("$Prefix=$Node")
    }
    return $out.ToArray()
}

function Compare-Config {
    param([string]$PublishedPath, [string]$DeployedPath)

    if (-not (Test-Path $PublishedPath) -or -not (Test-Path $DeployedPath)) { return }

    $published = Get-Content $PublishedPath -Raw | ConvertFrom-Json
    $deployed = Get-Content $DeployedPath -Raw | ConvertFrom-Json

    $pubLeaves = @(Get-JsonLeaf -Node $published -Prefix '' | Where-Object { $_ -notmatch '(^|/)//' })
    $depKeys = @(Get-JsonLeaf -Node $deployed -Prefix '' | ForEach-Object { ($_ -split '=', 2)[0] })
    $missing = @($pubLeaves | Where-Object { $depKeys -notcontains ($_ -split '=', 2)[0] })

    if ($missing.Count -eq 0) {
        Write-Ok 'appsettings: target has every key present in the published config'
        return
    }

    # Reported, never auto-applied: the deployed config is hand-tuned and ahead of source,
    # so a generic rewrite risks reformatting or dropping operator values.
    Write-Warn "appsettings: $($missing.Count) real key(s) exist in the build but NOT on the target:"
    foreach ($leaf in $missing) {
        $parts = $leaf -split '=', 2
        $value = $parts[1]
        if ($parts[0] -match 'password|secret|token|apikey|api_key|\bpat\b|credential') { $value = '***masked***' }
        Write-Info "  $($parts[0]) = $value"
    }
    Write-Info 'Add these by hand to the target appsettings.json, then re-run to confirm.'
}

function Invoke-PatchRollback {
    param([string]$TargetRoot, [string]$Stamp, [string]$Label, [switch]$DoIt)

    $root = Join-Path $TargetRoot '_patchbackup'
    if (-not (Test-Path $root)) { Write-Warn "$Label - no backups found"; return }

    if (-not $Stamp) {
        $latest = Get-ChildItem $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $latest) { Write-Warn "$Label - no backups found"; return }
        $Stamp = $latest.Name
    }

    $from = Join-Path $root $Stamp
    if (-not (Test-Path $from)) { Write-Bad "$Label - backup '$Stamp' not found"; return }

    $files = @(Get-ChildItem $from -Recurse -File)
    Write-Info "$Label - restoring $($files.Count) file(s) from $Stamp"
    if (-not $DoIt) { $files | ForEach-Object { Write-Info "  would restore $($_.Name)" }; return }

    $len = $from.Length + 1
    foreach ($file in $files) {
        $rel = $file.FullName.Substring($len)
        Copy-Item -Path $file.FullName -Destination (Join-Path $TargetRoot $rel) -Force
    }
    Write-Ok "$Label - restored $($files.Count) file(s)"
}

# ---------------------------------------------------------------- main

$components = @()
if ($Component -eq 'both') { $components = @('controller', 'web') } else { $components = @($Component) }

Write-Step "TestController surgical patch  ->  $Node"
Write-Info "component(s) : $($components -join ', ')"
Write-Info "mode         : $(if ($Apply) { 'APPLY (files will be written)' } else { 'DRY RUN (nothing will be written)' })"
Write-Info "configuration: $Configuration"

# ---- rollback path -------------------------------------------------------
if ($Rollback) {
    Write-Step 'ROLLBACK'
    foreach ($name in $components) {
        Invoke-PatchRollback -TargetRoot $Targets[$name].Path -Stamp $BackupStamp `
                             -Label $Targets[$name].Label -DoIt:$Apply
    }
    if (-not $Apply) { Write-Warn 'DRY RUN - re-run with -Apply to actually restore.' }
    return
}

# ---- reachability --------------------------------------------------------
Write-Step 'PRECHECK'
foreach ($name in $components) {
    if (Test-Path $Targets[$name].Path) { Write-Ok "$($Targets[$name].Label) share reachable" }
    else { throw "Cannot reach $($Targets[$name].Path). Check the share and your permissions." }
}

$controllerUp = Test-ControllerRunning -Machine $Node
if ($components -contains 'controller') {
    if ($controllerUp) {
        Write-Bad "Controller is RUNNING on $Node (port 5100/5200 open) - its assemblies are locked."
        Write-Info 'Close it from the TRAY ICON on the target (not Task Manager), then re-run.'
        if ($Apply) { throw 'Refusing to patch a running controller.' }
    }
    else { Write-Ok 'Controller is stopped - safe to patch' }
}

# ---- build ---------------------------------------------------------------
Write-Step 'BUILD'
foreach ($name in $components) {
    if ($SkipBuild -and (Test-Path $Targets[$name].Stage)) {
        Write-Warn "$($Targets[$name].Label) - reusing existing payload (-SkipBuild)"
        continue
    }
    Invoke-Publish -Project $Targets[$name].Project -OutDir $Targets[$name].Stage -Config $Configuration
}

# ---- compare + patch -----------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$anyFailure = $false

foreach ($name in $components) {
    $spec = $Targets[$name]
    Write-Step "$($spec.Label)  ->  $($spec.Path)"

    $skipWww = -not $IncludeSpa
    $changed = @(Get-ChangedFile -SourceRoot $spec.Stage -TargetRoot $spec.Path `
                                 -Protected $ProtectedFiles[$name] -SkipWwwroot:$skipWww)

    Write-Info "protected (never copied): $($ProtectedFiles[$name] -join ', ')"
    if ($name -eq 'web' -and -not $IncludeSpa) { Write-Info 'wwwroot skipped (pass -IncludeSpa to include the SPA)' }

    if ($changed.Count -eq 0) {
        Write-Ok 'Nothing to patch - target already matches this build.'
    }
    else {
        $real = @($changed | Where-Object { $_.Reason -ne 'rebuilt' })
        $rebuilt = @($changed | Where-Object { $_.Reason -eq 'rebuilt' })

        Write-Host ''
        if ($real.Count -gt 0) {
            Write-Host "  $($real.Count) file(s) with REAL content changes:" -ForegroundColor White
            foreach ($item in ($real | Sort-Object Rel)) {
                if ($item.Reason -eq 'new') { $delta = "NEW, $($item.New) bytes" }
                else { $delta = "$($item.Old) -> $($item.New) bytes" }
                Write-Host ("    {0,-52} {1}" -f $item.Rel, $delta) -ForegroundColor White
            }
        }
        else {
            Write-Ok 'No real content changes detected (no file changed size).'
        }

        if ($rebuilt.Count -gt 0) {
            Write-Info "$($rebuilt.Count) file(s) same size but new hash - recompiled only (MVID churn)."
            Write-Info 'These are copied too, so the target keeps one coherent build set.'
        }
    }

    # appsettings drift is reported for every run, apply or not
    Compare-Config -PublishedPath (Join-Path $spec.Stage 'appsettings.json') `
                   -DeployedPath (Join-Path $spec.Path 'appsettings.json')

    if ($changed.Count -eq 0) { continue }
    if (-not $Apply) { Write-Warn 'DRY RUN - nothing copied.'; continue }

    Backup-File -TargetRoot $spec.Path -Changed $changed -Stamp $stamp | Out-Null

    $offline = $null
    if ($name -eq 'web') {
        $offline = Join-Path $spec.Path 'app_offline.htm'
        Set-Content -Path $offline -Value '<html><body><h2>Updating - back shortly</h2></body></html>' -Encoding ASCII
        Write-Info 'app_offline.htm placed (releases IIS in-process DLL locks)'
    }

    try {
        $failed = @(Copy-Verified -SourceRoot $spec.Stage -TargetRoot $spec.Path -Changed $changed)
    }
    finally {
        if ($offline -and (Test-Path $offline)) {
            Remove-Item $offline -Force
            Write-Info 'app_offline.htm removed'
        }
    }

    if ($failed.Count -eq 0) {
        Write-Ok "patched and SHA256-verified $($changed.Count) file(s)"
    }
    else {
        $anyFailure = $true
        Write-Bad "$($failed.Count) file(s) failed:"
        $failed | ForEach-Object { Write-Bad "  $_" }
        Write-Info "Roll back with: .\deploy\Invoke-Patch.ps1 -Rollback -BackupStamp $stamp -Apply"
    }
}

# ---- verify --------------------------------------------------------------
if ($Apply -and $components -contains 'web') {
    Write-Step 'WEB HEALTH'
    foreach ($url in "http://$Node`:81/healthz/live", "http://$Node`:81/healthz/ready") {
        try {
            $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            Write-Ok "$url -> $($resp.StatusCode)"
        }
        catch {
            $anyFailure = $true
            Write-Bad "$url -> $($_.Exception.Message)"
        }
    }
}

Write-Step 'SUMMARY'
if (-not $Apply) {
    Write-Warn 'DRY RUN complete. Re-run with -Apply to write the changes.'
}
elseif ($anyFailure) {
    Write-Bad "Patch completed WITH FAILURES. Rollback stamp: $stamp"
    exit 1
}
else {
    Write-Ok "Patch complete. Rollback stamp: $stamp"
    if ($components -contains 'controller') {
        Write-Warn 'The controller is a desktop app with no scheduled task - START IT MANUALLY on the target.'
    }
}
