<#
.SYNOPSIS
    Surgical binary patch of the WPF controller on a target node.

.DESCRIPTION
    Copies ONLY the files whose content actually differs between the freshly published payload
    and the target, and NEVER copies anything in -Exclude. Unlike Invoke-FleetDeployment.ps1 this
    does not mirror, so target-only state (WatchList.xml, Parameters\, Logs\, orchestrator.db,
    commandpolicy.json, *.bat) is untouched by construction rather than by exclusion rules.

    Default run is a DRY RUN. Pass -Apply to actually copy.

.NOTES
    The controller must be CLOSED on the target: the running process locks its own assemblies.
    Close it from the tray icon - a force kill makes CrashDumpHelper write a ~626 MB dump.
#>
param(
    [string]$Source = 'publish\controller',
    [string]$Target = '\\JVGR22\C$\TestControllerService',
    [string]$Node = 'JVGR22',
    [string]$ProcessName = 'TestControllerGrpc',
    [string[]]$Exclude = @('appsettings.json'),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$srcFull = (Resolve-Path $Source).Path
$excludeSet = @{}
$Exclude | ForEach-Object { $excludeSet[$_.ToLowerInvariant()] = $true }

# ---- 1. Work out the minimal file set -------------------------------------------------
$changed = @()
$skipped = @()
foreach ($f in Get-ChildItem $srcFull -Recurse -File) {
    $rel = $f.FullName.Substring($srcFull.Length).TrimStart('\')
    if ($excludeSet.ContainsKey($rel.ToLowerInvariant())) { $skipped += $rel; continue }

    $dst = Join-Path $Target $rel
    if (-not (Test-Path $dst)) { $changed += $rel; continue }
    if ((Get-FileHash $f.FullName -Algorithm SHA256).Hash -ne (Get-FileHash $dst -Algorithm SHA256).Hash) {
        $changed += $rel
    }
}

"Payload : $srcFull"
"Target  : $Target"
"Excluded (never copied): $($skipped -join ', ')"
""
"Files to patch: $($changed.Count)"
$changed | Sort-Object | ForEach-Object { "   $_" }
""

if ($changed.Count -eq 0) { 'Nothing to do.'; return }

# ---- 2. Refuse to touch a running controller ------------------------------------------
$running = Invoke-Command -ComputerName $Node -ScriptBlock {
    param($n) $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) { "pid=$($p.Id) since $($p.StartTime)" } else { $null }
} -ArgumentList $ProcessName

if ($running) {
    "CONTROLLER IS RUNNING ON ${Node}: $running"
    "Its assemblies are locked. Close it from the tray icon on $Node, then re-run with -Apply."
    if ($Apply) { throw "Refusing to patch while $ProcessName is running on $Node." }
}

if (-not $Apply) { ''; 'DRY RUN - nothing copied. Re-run with -Apply once the controller is closed.'; return }

# ---- 3. Back up exactly what we are about to overwrite --------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $Target "_patchbackup\$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($rel in $changed) {
    $dst = Join-Path $Target $rel
    if (Test-Path $dst) { Copy-Item $dst (Join-Path $backup $rel) -Force }
}
"Backed up $($changed.Count) file(s) to $backup"

# ---- 4. Copy, then verify every file by hash ------------------------------------------
$failed = @()
foreach ($rel in $changed) {
    Copy-Item (Join-Path $srcFull $rel) (Join-Path $Target $rel) -Force
    $a = (Get-FileHash (Join-Path $srcFull $rel) -Algorithm SHA256).Hash
    $b = (Get-FileHash (Join-Path $Target $rel) -Algorithm SHA256).Hash
    if ($a -ne $b) { $failed += $rel }
}

if ($failed.Count) {
    "VERIFY FAILED for: $($failed -join ', ')"
    "Roll back with: Copy-Item '$backup\*' '$Target' -Force"
    throw 'Patch verification failed.'
}

"Patched and verified $($changed.Count) file(s)."
"Rollback: Copy-Item '$backup\*' '$Target' -Force"
"NEXT: start $ProcessName on $Node (launchMode is Manual - it cannot be started remotely)."
