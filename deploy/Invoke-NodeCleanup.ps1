<#
.SYNOPSIS
    Local node housekeeping for a TestAgentSolution agent or controller.

.DESCRIPTION
    Runs ON the node, as Administrator. Dry run unless -Apply is given.

    Protected state is the SAME list the fleet deployment protects: it is read from
    deploy\fleet-inventory.json when that file sits next to this script, and falls back to an
    embedded copy otherwise (the node does not have the repo). Invoke-NodeCleanup.Tests.ps1 fails
    if the embedded copy drifts from the inventory, so the two cannot diverge silently.

    Deletion rules, deliberately narrow:
      * A preserved FILE is never deleted, wherever it lives.
      * A preserved FOLDER is never removed, but files inside it may be pruned when they match an
        explicit junk CATEGORY - that is the only way log pruning can work, since every log lives
        under Logs\, which is preserved.
      * Only category matches are ever deleted. Nothing is selected by size, and runtimes\ (a
        deployment artifact, ~464 MB on the controller) is never touched.
      * *.jsonl audit logs are reported, never pruned.
      * Nothing outside -InstallRoot is deleted, enforced on the resolved full path.

.PARAMETER Role
    Agent or Controller. Controller is never stopped by this script.

.PARAMETER Apply
    Perform changes. Without it the script only reports what it would do.

.PARAMETER LogRetentionDays
    Age threshold for log pruning.

.PARAMETER DumpRetentionDays
    Age threshold for *.dmp. Dumps newer than this are kept and listed as crash evidence.

.PARAMETER ManifestPath
    Manifest from New-NodeManifest.ps1. Omit to skip the version comparison.

.PARAMETER ConfigPatchPath
    JSON file of { "set": { "Dotted.Key": value } }. Omit to skip the config step.

.PARAMETER StartAfter
    Agent only: start the agent afterwards and wait for its port.

.PARAMETER ReportFolder
    Where the HTML and JSON reports are written. Config backups go to <ReportFolder>\backups\<stamp>\,
    deliberately outside the install root so a later robocopy /MIR cannot delete them.

.PARAMETER InstallRoot
    Install folder. Defaults by role. Also the deletion boundary.

.PARAMETER InventoryPath
    fleet-inventory.json supplying the protected lists. Auto-detected next to this script.

.PARAMETER BackupKeep
    Number of newest backup sets to keep in each backup folder.

.PARAMETER BackupFolders
    Backup folder names subject to retention.

.PARAMETER StopTimeoutSeconds
    How long to wait for the agent process to exit.

.EXAMPLE
    .\Invoke-NodeCleanup.ps1 -Role Agent
.EXAMPLE
    .\Invoke-NodeCleanup.ps1 -Role Agent -Apply -ConfigPatchPath .\patch.json -StartAfter
.EXAMPLE
    .\Invoke-NodeCleanup.ps1 -Role Controller -Apply
#>
[CmdletBinding()]
param(
    [ValidateSet('Agent', 'Controller')]
    [string]$Role = 'Agent',

    [switch]$Apply,

    [ValidateRange(0, 3650)]
    [int]$LogRetentionDays = 14,

    [ValidateRange(0, 3650)]
    [int]$DumpRetentionDays = 7,

    [string]$ManifestPath,
    [string]$ConfigPatchPath,
    [switch]$StartAfter,

    [string]$ReportFolder = 'C:\NodeCleanupReports',
    [string]$InstallRoot,
    [string]$InventoryPath,

    [ValidateRange(0, 100)]
    [int]$BackupKeep = 3,

    [string[]]$BackupFolders = @('_patchbackup', '_dllbackup'),

    [ValidateRange(5, 3600)]
    [int]$StopTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Embedded fallback, used ONLY when fleet-inventory.json is not next to this script.
# GENERATED from deploy\fleet-inventory.json by deploy\Sync-PreserveList.ps1 -
# do not hand-edit between the markers; edit the inventory and re-run the generator.
# Invoke-NodeCleanup.Tests.ps1 fails if this block does not match the inventory.
#
# WatchList.xml and Prepare-Agent.bat are CONTROLLER-ONLY. Listing them here is harmless on an
# agent and keeps one list for both roles - their absence from an agent is normal, not a loss.
# ---------------------------------------------------------------------------
# <PreserveList:BEGIN>
$script:FallbackPreserveFiles = @(
    'appsettings.json',
    'commandpolicy.json',
    '*.bak-*',
    'WatchList.xml',
    'Prepare-Agent.bat',
    '*.bat',
    '*.cmd',
    'orchestrator.db',
    'orchestrator.db-wal',
    'orchestrator.db-shm',
    'impact-index.db',
    'impact-index.db-wal',
    'impact-index.db-shm',
    'impact-outcomes.db',
    'impact-outcomes.db-wal',
    'impact-outcomes.db-shm',
    'libSkiaSharp.dll',
    'libHarfBuzzSharp.dll',
    'e_sqlite3.dll',
    'LdaNative.dll'
)
$script:FallbackPreserveFolders = @('Logs', 'Data', 'Parameters', 'maintenance-logs', '_patchbackup', '_dllbackup')
# <PreserveList:END>

# Never pruned, whatever else matches. runtimes\ is a deployment artifact, not junk.
$script:NeverTouchFolders = @('runtimes')
$script:ReportOnlyPatterns = @('*.jsonl')

# ---------------------------------------------------------------------------
# State
# ---------------------------------------------------------------------------
$script:Log       = New-Object System.Collections.Generic.List[object]
$script:Actions   = New-Object System.Collections.Generic.List[object]
$script:Deletions = New-Object System.Collections.Generic.List[object]
$script:KeptDumps = New-Object System.Collections.Generic.List[object]
$script:Warnings  = New-Object System.Collections.Generic.List[string]
$script:Failures  = New-Object System.Collections.Generic.List[string]
$script:Findings  = New-Object System.Collections.Generic.List[object]
$script:ConfigChanges = New-Object System.Collections.Generic.List[object]
$script:VersionDiff   = New-Object System.Collections.Generic.List[object]

function Write-Log {
    param([string]$Message, [ValidateSet('INFO', 'OK', 'WARN', 'FAIL', 'STEP', 'PLAN')][string]$Level = 'INFO')

    $stamp = (Get-Date).ToString('HH:mm:ss')
    $script:Log.Add([pscustomobject]@{ Time = $stamp; Level = $Level; Message = $Message })

    $colour = switch ($Level) {
        'OK'   { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'STEP' { 'Cyan' }
        'PLAN' { 'Magenta' }
        default { 'Gray' }
    }
    Write-Host ("[{0}] [{1,-4}] {2}" -f $stamp, $Level, $Message) -ForegroundColor $colour

    if ($Level -eq 'WARN') { $script:Warnings.Add($Message) }
    if ($Level -eq 'FAIL') { $script:Failures.Add($Message) }
}

function Add-Action {
    param([string]$Category, [string]$Detail, [double]$Bytes = 0)
    $script:Actions.Add([pscustomobject]@{ Category = $Category; Detail = $Detail; Bytes = $Bytes })
}

function Add-Finding {
    param([string]$Area, [string]$Name, [string]$Value, [string]$Severity = 'info')
    $script:Findings.Add([pscustomobject]@{ Area = $Area; Name = $Name; Value = $Value; Severity = $Severity })
}

# ---------------------------------------------------------------------------
# Pure helpers - unit tested via AST extraction, no filesystem side effects
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    Runs a native executable without letting its stderr become a terminating error.
.DESCRIPTION
    With $ErrorActionPreference = 'Stop', anything a native tool writes to stderr is promoted to a
    terminating NativeCommandError. schtasks does exactly that for the perfectly normal "task does
    not exist" case, which aborted the whole run at step 2.
#>
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = @($output) }
    }
    finally { $ErrorActionPreference = $previous }
}

<#
.SYNOPSIS
    Sums a property across a collection, returning 0 for an empty one.
.DESCRIPTION
    Measure-Object -Sum throws "Argument types do not match" when handed an empty collection with a
    property name, and $ErrorActionPreference = 'Stop' makes that fatal. A clean node has nothing to
    delete, so every sum here is reached with an empty list on the happy path.
#>
function Get-TotalBytes {
    param($Items, [string]$Property = 'Bytes')

    # @($null) is a ONE element array containing $null, so a bare .Count check still lets a null
    # through to Measure-Object, which is the very thing this guard exists to avoid.
    $list = @($Items | Where-Object { $null -ne $_ })
    if ($list.Count -eq 0) { return [double]0 }
    $sum = ($list | Measure-Object -Property $Property -Sum).Sum
    if ($null -eq $sum) { return [double]0 }
    return [double]$sum
}

<#
.SYNOPSIS
    True when Candidate resolves inside Root. The deletion boundary.
.DESCRIPTION
    Compares normalised full paths, so "C:\X\..\Y" and a trailing separator cannot smuggle a path
    out of the root. A prefix test alone would let "C:\TestAgentServiceEVIL" pass as inside
    "C:\TestAgentService", hence the explicit separator.
#>
function Test-PathInside {
    param([string]$Candidate, [string]$Root)

    if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }

    try {
        $c = [System.IO.Path]::GetFullPath($Candidate)
        $r = [System.IO.Path]::GetFullPath($Root)
    } catch { return $false }

    $r = $r.TrimEnd('\', '/')
    if ($c.Equals($r, [StringComparison]::OrdinalIgnoreCase)) { return $false }  # the root itself is not deletable
    return $c.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

<#
.SYNOPSIS
    True when a file name matches any preserve pattern.
.DESCRIPTION
    Same name, same body, same wildcard semantics as Test-IsPreserved in Invoke-FleetDeployment.ps1.
    Both scripts must agree on what is protected, so keep the two copies identical.
#>
function Test-IsPreserved {
    param([string]$Name, [string[]]$Patterns)

    foreach ($p in $Patterns) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        if ($Name -like $p) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    Classifies one file into a junk category, or $null to keep it.
.DESCRIPTION
    Category is the ONLY reason a file is ever deleted. Order matters: preserve and report-only
    win over every category.

    Crash dumps are age-gated separately from logs: a recent .dmp is the evidence for whatever just
    went wrong, and a 748 MB dump on JVGR22 turned out to be exactly that. *.tmp has no such value,
    so it goes at any age.
#>
function Get-JunkCategory {
    param(
        [string]$Name,
        [datetime]$LastWriteTime,
        [datetime]$Now,
        [int]$RetentionDays,
        [string[]]$PreservePatterns,
        [int]$DumpRetentionDays = 7
    )

    if (Test-IsPreserved -Name $Name -Patterns $PreservePatterns) { return $null }
    if (Test-IsPreserved -Name $Name -Patterns $script:ReportOnlyPatterns) { return $null }

    if ($Name -like '*.dmp') {
        if (($Now - $LastWriteTime).TotalDays -gt $DumpRetentionDays) { return 'CrashDumps' }
        return $null
    }
    if ($Name -like '*.tmp') { return 'TempFiles' }

    # Rotated logs look like app.log.1 / app_2026-10-02.log; both end up here.
    if ($Name -like '*.log' -or $Name -like '*.log.*') {
        if (($Now - $LastWriteTime).TotalDays -gt $RetentionDays) { return 'OldLogs' }
    }
    return $null
}

<# .SYNOPSIS True when a dump is recent enough to be worth keeping as crash evidence. #>
function Test-RecentCrashDump {
    param([string]$Name, [datetime]$LastWriteTime, [datetime]$Now, [int]$DumpRetentionDays)

    if ($Name -notlike '*.dmp') { return $false }
    return (($Now - $LastWriteTime).TotalDays -le $DumpRetentionDays)
}

<# .SYNOPSIS Backup set folder names to delete, keeping the newest $Keep by name (timestamped). #>
function Select-BackupSetsToDelete {
    param([string[]]$SetNames, [int]$Keep)

    if ($null -eq $SetNames -or $SetNames.Count -eq 0) { return @() }
    if ($Keep -lt 0) { $Keep = 0 }

    $ordered = @($SetNames | Sort-Object -Descending)
    if ($ordered.Count -le $Keep) { return @() }
    return @($ordered[$Keep..($ordered.Count - 1)])
}

<#
.SYNOPSIS
    Compares a node folder listing against a manifest.
.DESCRIPTION
    'Missing' means the manifest names a file the node does not have. Before treating one as a loss,
    check the manifest was built for THIS role: WatchList.xml and Prepare-Agent.bat are controller-only
    and have never existed on an agent, so a controller manifest compared against an agent reports both
    as Missing when nothing is wrong.
.OUTPUTS
    Objects with Status = Missing | Extra | Different.
#>
function Compare-NodeManifest {
    param($Manifest, $Actual, [string[]]$IgnorePatterns)

    $result = New-Object System.Collections.Generic.List[object]

    $manMap = @{}
    foreach ($m in $Manifest) { $manMap[$m.Path.ToLowerInvariant()] = $m }
    $actMap = @{}
    foreach ($a in $Actual) { $actMap[$a.Path.ToLowerInvariant()] = $a }

    foreach ($key in $manMap.Keys) {
        $m = $manMap[$key]
        if (-not $actMap.ContainsKey($key)) {
            $result.Add([pscustomobject]@{ Path = $m.Path; Status = 'Missing'; Expected = $m.Sha256; Found = '' })
            continue
        }
        $a = $actMap[$key]
        if ($m.Sha256 -and $a.Sha256 -and $m.Sha256 -ne $a.Sha256) {
            $result.Add([pscustomobject]@{ Path = $m.Path; Status = 'Different'; Expected = $m.Sha256; Found = $a.Sha256 })
        }
    }

    foreach ($key in $actMap.Keys) {
        if ($manMap.ContainsKey($key)) { continue }
        $leaf = Split-Path $actMap[$key].Path -Leaf
        if (Test-IsPreserved -Name $leaf -Patterns $IgnorePatterns) { continue }
        $result.Add([pscustomobject]@{ Path = $actMap[$key].Path; Status = 'Extra'; Expected = ''; Found = $actMap[$key].Sha256 })
    }

    return @($result | Sort-Object Status, Path)
}

<# .SYNOPSIS Reads a dotted path out of a parsed JSON object. Returns $null when absent. #>
function Get-DottedValue {
    param($Root, [string]$Path)

    $node = $Root
    foreach ($seg in $Path.Split('.')) {
        if ($null -eq $node) { return $null }
        $prop = $node.PSObject.Properties[$seg]
        if ($null -eq $prop) { return $null }
        $node = $prop.Value
    }
    return $node
}

<#
.SYNOPSIS
    Replaces one scalar value in JSON TEXT, leaving all other bytes untouched.
.DESCRIPTION
    A ConvertFrom-Json / ConvertTo-Json round trip would reformat the whole file, so the edit is
    made textually: walk the dotted path narrowing to each ancestor object's brace span, then
    replace just the leaf's value token. Returns $null when the path is not found.
#>
function Set-JsonValuePreservingFormat {
    param([string]$Text, [string]$Path, $NewValue)

    $segments = $Path.Split('.')
    $spanStart = 0
    $spanEnd = $Text.Length - 1

    for ($i = 0; $i -lt $segments.Count - 1; $i++) {
        $keyPattern = '"' + [regex]::Escape($segments[$i]) + '"\s*:\s*\{'
        $m = [regex]::Match($Text.Substring($spanStart, $spanEnd - $spanStart + 1), $keyPattern)
        if (-not $m.Success) { return $null }

        $braceOpen = $spanStart + $m.Index + $m.Length - 1
        $depth = 0
        $close = -1
        for ($p = $braceOpen; $p -le $spanEnd; $p++) {
            if ($Text[$p] -eq '{') { $depth++ }
            elseif ($Text[$p] -eq '}') {
                $depth--
                if ($depth -eq 0) { $close = $p; break }
            }
        }
        if ($close -lt 0) { return $null }
        $spanStart = $braceOpen + 1
        $spanEnd = $close - 1
    }

    $leaf = $segments[$segments.Count - 1]
    $leafPattern = '("' + [regex]::Escape($leaf) + '"\s*:\s*)(""|"(?:[^"\\]|\\.)*"|true|false|null|-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)'
    $scope = $Text.Substring($spanStart, $spanEnd - $spanStart + 1)
    $lm = [regex]::Match($scope, $leafPattern)
    if (-not $lm.Success) { return $null }

    if ($NewValue -is [bool])            { $rendered = $(if ($NewValue) { 'true' } else { 'false' }) }
    elseif ($null -eq $NewValue)         { $rendered = 'null' }
    elseif ($NewValue -is [int] -or $NewValue -is [long] -or $NewValue -is [double] -or $NewValue -is [decimal]) {
        $rendered = [string]$NewValue
    }
    else { $rendered = '"' + ([string]$NewValue).Replace('\', '\\').Replace('"', '\"') + '"' }

    $absoluteIndex = $spanStart + $lm.Index
    $prefixLen = $lm.Groups[1].Length
    $valueStart = $absoluteIndex + $prefixLen
    $valueLen = $lm.Groups[2].Length

    return $Text.Substring(0, $valueStart) + $rendered + $Text.Substring($valueStart + $valueLen)
}

# ---------------------------------------------------------------------------
# Impure steps
# ---------------------------------------------------------------------------

function Get-PreserveLists {
    param([string]$ExplicitPath)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($ExplicitPath) { [void]$candidates.Add($ExplicitPath) }
    [void]$candidates.Add((Join-Path $PSScriptRoot 'fleet-inventory.json'))
    [void]$candidates.Add((Join-Path $PSScriptRoot 'deploy\fleet-inventory.json'))

    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        try {
            $inv = Get-Content $c -Raw | ConvertFrom-Json
            $files = @($inv.preserveFiles)
            $folders = @($inv.preserveFolders)
            if ($files.Count -gt 0 -and $folders.Count -gt 0) {
                Write-Log "Protected lists read from $c ($($files.Count) file patterns, $($folders.Count) folders)." 'OK'
                return @{ Files = $files; Folders = $folders; Source = $c }
            }
        } catch {
            Write-Log "Inventory at $c unreadable, falling back: $($_.Exception.Message)" 'WARN'
        }
    }

    Write-Log "No inventory found; using the embedded protected lists." 'INFO'
    return @{ Files = $script:FallbackPreserveFiles; Folders = $script:FallbackPreserveFolders; Source = 'embedded' }
}

function Test-Administrator {
    if ($env:TC_FAKE_ADMIN -eq '1') { return $true }
    if ($env:TC_FAKE_ADMIN -eq '1') { return $true }
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Precheck {
    param([string]$Root)

    Write-Log 'STEP 1 - precheck' 'STEP'

    if (-not (Test-Administrator)) {
        Write-Log 'Not running as Administrator. Re-run from an elevated prompt.' 'FAIL'
        return $false
    }
    Write-Log "Administrator: yes" 'OK'
    Write-Log "Host: $env:COMPUTERNAME   Role: $Role   Mode: $(if ($Apply) { 'APPLY' } else { 'DRY RUN' })" 'INFO'

    if (-not (Test-Path $Root)) {
        Write-Log "Install root not found: $Root" 'FAIL'
        return $false
    }
    Write-Log "Install root: $Root" 'OK'

    $drive = (Get-Item $Root).PSDrive.Name
    $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='${drive}:'"
    $freeGb = [math]::Round($disk.FreeSpace / 1GB, 1)
    Write-Log ("Disk {0}: {1} GB free of {2} GB" -f $drive, $freeGb, [math]::Round($disk.Size / 1GB, 1)) 'INFO'
    Add-Finding 'Disk' "${drive}: free" "$freeGb GB" $(if ($freeGb -lt 5) { 'warn' } else { 'info' })
    if ($freeGb -lt 5) { Write-Log "Low disk space: $freeGb GB free." 'WARN' }

    # Config backup happens before anything else touches the node.
    #
    # Written OUTSIDE $Root on purpose. These used to land beside the original as
    # <name>.bak-<stamp>, which put them inside the folder the fleet deploy mirrors - robocopy /MIR
    # deleted a commandpolicy.json backup 20 seconds after it was taken. Out here, no deploy can
    # reach them no matter what the preserve list says.
    #
    # WatchList.xml is controller-only; an agent simply has no such file and skips it.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path (Join-Path $ReportFolder 'backups') $stamp
    foreach ($cfgName in @('appsettings.json', 'commandpolicy.json', 'WatchList.xml')) {
        $cfg = Join-Path $Root $cfgName
        if (-not (Test-Path $cfg)) { continue }
        $dest = Join-Path $backupDir $cfgName
        if ($Apply) {
            if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
            Copy-Item $cfg $dest -Force
            Write-Log "Backed up $cfgName -> $dest" 'OK'
        } else {
            Write-Log "Would back up $cfgName -> $dest" 'PLAN'
        }
        Add-Action 'ConfigBackup' "$cfgName -> $dest"
    }
    return $true
}

function Stop-NodeProcesses {
    param([string]$Root)

    Write-Log 'STEP 2 - stop' 'STEP'

    if ($Role -eq 'Controller') {
        $proc = @(Get-Process -Name 'TestControllerGrpc' -ErrorAction SilentlyContinue)
        if ($proc.Count -gt 0) {
            Write-Log 'Close the controller from the tray first. This script will not stop it.' 'FAIL'
            return $false
        }
        Write-Log 'Controller is not running - safe to continue.' 'OK'
        return $true
    }

    $taskName = 'TestAgentGrpc Interactive'
    $query = Invoke-Native -Exe 'schtasks' -Arguments @('/Query', '/TN', $taskName)
    if ($query.ExitCode -eq 0) {
        if ($Apply) {
            Invoke-Native -Exe 'schtasks' -Arguments @('/End', '/TN', $taskName) | Out-Null
            Write-Log "Scheduled task stopped: $taskName" 'OK'
        } else {
            Write-Log "Would stop scheduled task: $taskName" 'PLAN'
        }
        Add-Action 'Stop' "scheduled task $taskName"
    } else {
        Write-Log "Scheduled task not present: $taskName" 'INFO'
    }

    $svc = Get-Service -Name 'TestAgentGrpc' -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        if ($Apply) {
            Stop-Service -Name 'TestAgentGrpc' -Force
            Write-Log 'Service TestAgentGrpc stopped.' 'OK'
        } else {
            Write-Log 'Would stop service TestAgentGrpc.' 'PLAN'
        }
        Add-Action 'Stop' 'service TestAgentGrpc'
    }

    if (-not $Apply) {
        Write-Log 'Dry run - not waiting for process exit.' 'INFO'
        return $true
    }

    $deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Name 'TestAgentGrpc' -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }

    $still = @(Get-Process -Name 'TestAgentGrpc' -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Write-Log "TestAgentGrpc still running after ${StopTimeoutSeconds}s." 'FAIL'
        return $false
    }
    Write-Log 'TestAgentGrpc process has exited.' 'OK'

    $listening = @(Get-NetTCPConnection -LocalPort 5200 -State Listen -ErrorAction SilentlyContinue)
    if ($listening.Count -gt 0) {
        Write-Log 'Port 5200 is still listening after stop.' 'WARN'
    } else {
        Write-Log 'Port 5200 is free.' 'OK'
    }
    return $true
}

function Invoke-Clean {
    param([string]$Root, $Preserve)

    Write-Log 'STEP 3 - clean' 'STEP'
    $now = Get-Date

    $neverFull = @()
    foreach ($n in $script:NeverTouchFolders) { $neverFull += (Join-Path $Root $n) }

    $all = @(Get-ChildItem $Root -Recurse -File -Force -ErrorAction SilentlyContinue)
    foreach ($f in $all) {
        $skip = $false
        foreach ($nt in $neverFull) {
            if ($f.FullName.StartsWith($nt + '\', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        if ($skip) { continue }

        $cat = Get-JunkCategory -Name $f.Name -LastWriteTime $f.LastWriteTime -Now $now `
                                -RetentionDays $LogRetentionDays -PreservePatterns $Preserve.Files `
                                -DumpRetentionDays $DumpRetentionDays
        if (-not $cat) {
            if (Test-RecentCrashDump -Name $f.Name -LastWriteTime $f.LastWriteTime -Now $now -DumpRetentionDays $DumpRetentionDays) {
                $script:KeptDumps.Add([pscustomobject]@{
                    Path     = $f.FullName
                    Bytes    = $f.Length
                    Modified = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
                    AgeDays  = [math]::Round(($now - $f.LastWriteTime).TotalDays, 1)
                })
            }
            continue
        }

        if (-not (Test-PathInside -Candidate $f.FullName -Root $Root)) {
            Write-Log "REFUSED (outside install root): $($f.FullName)" 'WARN'
            continue
        }

        $script:Deletions.Add([pscustomobject]@{
            Category = $cat
            Path     = $f.FullName
            Bytes    = $f.Length
            Modified = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        })
    }

    foreach ($bf in $BackupFolders) {
        $bfPath = Join-Path $Root $bf
        if (-not (Test-Path $bfPath)) {
            Write-Log "Backup folder absent (no-op): $bf" 'INFO'
            continue
        }
        $sets = @(Get-ChildItem $bfPath -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $doomed = Select-BackupSetsToDelete -SetNames $sets -Keep $BackupKeep
        Write-Log "$bf : $($sets.Count) set(s), keeping newest $BackupKeep, removing $($doomed.Count)" 'INFO'

        foreach ($d in $doomed) {
            $full = Join-Path $bfPath $d
            if (-not (Test-PathInside -Candidate $full -Root $Root)) {
                Write-Log "REFUSED (outside install root): $full" 'WARN'
                continue
            }
            $size = Get-TotalBytes -Items (Get-ChildItem $full -Recurse -File -ErrorAction SilentlyContinue) -Property 'Length'
            $script:Deletions.Add([pscustomobject]@{
                Category = 'BackupSets'
                Path     = $full
                Bytes    = $size
                Modified = ''
            })
        }
    }

    foreach ($d in $script:Deletions) {
        if ($Apply) {
            try {
                Remove-Item -LiteralPath $d.Path -Recurse -Force -ErrorAction Stop
                Write-Log ("Deleted [{0}] {1} ({2:N1} KB)" -f $d.Category, $d.Path, ($d.Bytes / 1KB)) 'OK'
            } catch {
                Write-Log "Could not delete $($d.Path): $($_.Exception.Message)" 'WARN'
            }
        } else {
            Write-Log ("Would delete [{0}] {1} ({2:N1} KB)" -f $d.Category, $d.Path, ($d.Bytes / 1KB)) 'PLAN'
        }
    }

    $byCat = @($script:Deletions | Group-Object Category)
    foreach ($g in $byCat) {
        $bytes = Get-TotalBytes -Items $g.Group
        Add-Action 'Clean' "$($g.Name): $($g.Count) item(s)" $bytes
        Write-Log ("{0,-12} {1,4} item(s)  {2,9:N1} MB" -f $g.Name, $g.Count, ($bytes / 1MB)) 'INFO'
    }
    if ($script:Deletions.Count -eq 0) { Write-Log 'Nothing to clean.' 'OK' }

    if ($script:KeptDumps.Count -gt 0) {
        $kb = Get-TotalBytes -Items $script:KeptDumps
        Write-Log ("{0} crash dump(s) newer than {1} day(s) KEPT as recent crash evidence ({2:N1} MB):" -f `
                   $script:KeptDumps.Count, $DumpRetentionDays, ($kb / 1MB)) 'INFO'
        foreach ($d in $script:KeptDumps) {
            Write-Log ("  kept {0} ({1:N1} MB, {2}, {3} day(s) old)" -f `
                       $d.Path, ($d.Bytes / 1MB), $d.Modified, $d.AgeDays) 'INFO'
        }
        Add-Finding 'CrashDumps' "kept - recent crash evidence" `
            ("{0} dump(s), {1:N1} MB, newer than {2} day(s)" -f $script:KeptDumps.Count, ($kb / 1MB), $DumpRetentionDays)
    }

    $audit = @(Get-ChildItem $Root -Recurse -File -Include $script:ReportOnlyPatterns -ErrorAction SilentlyContinue)
    if ($audit.Count -gt 0) {
        $ab = Get-TotalBytes -Items $audit -Property 'Length'
        Add-Finding 'Audit' 'jsonl files (never pruned)' ("{0} files, {1:N1} MB" -f $audit.Count, ($ab / 1MB))
    }
}

function Invoke-VersionCheck {
    param([string]$Root, [string]$Path, $Preserve)

    Write-Log 'STEP 4 - version check' 'STEP'
    if (-not $Path) { Write-Log 'No -ManifestPath supplied; skipped.' 'INFO'; return }
    if (-not (Test-Path $Path)) { Write-Log "Manifest not found: $Path" 'WARN'; return }

    $manifest = (Get-Content $Path -Raw | ConvertFrom-Json).Files
    $actual = New-Object System.Collections.Generic.List[object]
    foreach ($f in (Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($Root.TrimEnd('\').Length + 1)
        $skip = $false
        foreach ($pf in $Preserve.Folders) {
            if ($rel.StartsWith($pf + '\', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        foreach ($nt in $script:NeverTouchFolders) {
            if ($rel.StartsWith($nt + '\', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        if ($skip) { continue }
        $actual.Add([pscustomobject]@{
            Path   = $rel
            Sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        })
    }

    $diff = Compare-NodeManifest -Manifest $manifest -Actual $actual -IgnorePatterns $Preserve.Files
    foreach ($d in $diff) { $script:VersionDiff.Add($d) }

    if ($diff.Count -eq 0) {
        Write-Log "Node matches the manifest ($($manifest.Count) files)." 'OK'
    } else {
        $counts = @($diff | Group-Object Status | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '
        Write-Log "Manifest differences: $counts (report only, no files copied)." 'WARN'
        foreach ($d in ($diff | Select-Object -First 25)) {
            Write-Log ("  {0,-10} {1}" -f $d.Status, $d.Path) 'INFO'
        }
    }
}

function Invoke-ConfigPatch {
    param([string]$Root, [string]$Path)

    Write-Log 'STEP 5 - config' 'STEP'
    if (-not $Path) { Write-Log 'No -ConfigPatchPath supplied; skipped.' 'INFO'; return }
    if (-not (Test-Path $Path)) { Write-Log "Config patch not found: $Path" 'WARN'; return }

    $patch = Get-Content $Path -Raw | ConvertFrom-Json
    if (-not $patch.set) { Write-Log 'Patch has no "set" section; nothing to do.' 'WARN'; return }

    $cfgPath = Join-Path $Root 'appsettings.json'
    if (-not (Test-Path $cfgPath)) { Write-Log "appsettings.json not found in $Root" 'WARN'; return }

    $text = Get-Content $cfgPath -Raw
    $parsed = $text | ConvertFrom-Json
    $changed = $false

    foreach ($prop in $patch.set.PSObject.Properties) {
        $key = $prop.Name
        $new = $prop.Value
        $old = Get-DottedValue -Root $parsed -Path $key

        if ($null -ne $old -and ([string]$old) -eq ([string]$new)) {
            Write-Log "  $key already '$new' - skipped." 'INFO'
            continue
        }

        $updated = Set-JsonValuePreservingFormat -Text $text -Path $key -NewValue $new
        if ($null -eq $updated) {
            Write-Log "  $key not found in appsettings.json - skipped." 'WARN'
            continue
        }

        $text = $updated
        $changed = $true
        $shownOld = $(if ($null -eq $old) { '(absent)' } else { [string]$old })
        Write-Log "  $key : '$shownOld' -> '$new'" $(if ($Apply) { 'OK' } else { 'PLAN' })
        $script:ConfigChanges.Add([pscustomobject]@{ Key = $key; Old = $shownOld; New = [string]$new })
        Add-Action 'Config' "$key : $shownOld -> $new"
    }

    if (-not $changed) { Write-Log 'Config already correct.' 'OK'; return }

    if ($Apply) {
        # No BOM: the file was written without one, and adding one breaks some JSON readers.
        [System.IO.File]::WriteAllText($cfgPath, $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Log 'appsettings.json updated (formatting preserved).' 'OK'
    } else {
        Write-Log 'Dry run - appsettings.json not written.' 'PLAN'
    }
}

function Invoke-HealthChecks {
    param([string]$Root)

    Write-Log 'STEP 6 - health (report only)' 'STEP'

    $dotnet = Invoke-Native -Exe 'dotnet' -Arguments @('--list-runtimes')
    if ($dotnet.ExitCode -eq 0) {
        $ten = @($dotnet.Output | Where-Object { "$_" -match '\s10\.' })
        Add-Finding 'Runtime' 'dotnet runtimes (10.x)' ("{0} entries" -f $ten.Count)
        foreach ($r in $ten) { Write-Log "  $r" 'INFO' }
        if ($ten.Count -eq 0) { Write-Log 'No .NET 10 runtime found.' 'WARN' }
    } else {
        Write-Log 'dotnet is not on PATH.' 'WARN'
        Add-Finding 'Runtime' 'dotnet' 'not available' 'warn'
    }

    $rc = @(Get-ChildItem $Root -Filter '*.runtimeconfig.json' -File -ErrorAction SilentlyContinue)
    foreach ($f in $rc) {
        try {
            $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $kind = $(if ($j.runtimeOptions.PSObject.Properties['includedFrameworks']) { 'self-contained' } else { 'framework-dependent' })
            Add-Finding 'Runtime' $f.Name $kind
            Write-Log "  $($f.Name): $kind" 'INFO'
        } catch {
            Write-Log "  $($f.Name): unreadable" 'WARN'
        }
    }

    try {
        $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Action -eq 'Block' -and $_.Enabled -eq 'True' })
        $blocking = New-Object System.Collections.Generic.List[string]
        foreach ($r in $rules) {
            $app = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
            if ($app -and $app.Program -and $app.Program -match 'testagentgrpc\.exe') {
                [void]$blocking.Add($r.DisplayName)
            }
        }
        if ($blocking.Count -gt 0) {
            Write-Log "Firewall BLOCK rules for testagentgrpc.exe: $($blocking -join ', ')" 'WARN'
            Add-Finding 'Firewall' 'Block rules for agent' ($blocking -join ', ') 'warn'
        } else {
            Add-Finding 'Firewall' 'Block rules for agent' 'none'
        }

        $allow = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
                   Where-Object { $_.Action -eq 'Allow' -and $_.Enabled -eq 'True' -and $_.DisplayName -match 'TestAgent' })
        foreach ($a in $allow) {
            Add-Finding 'Firewall' "Allow: $($a.DisplayName)" "profiles=$($a.Profile) dir=$($a.Direction)"
        }
        if ($allow.Count -eq 0) { Add-Finding 'Firewall' 'Allow rules matching TestAgent' 'none' 'warn' }
    } catch {
        Write-Log "Firewall query failed: $($_.Exception.Message)" 'WARN'
    }

    try {
        $winrm = Get-Service -Name WinRM -ErrorAction Stop
        Add-Finding 'WinRM' 'Service' "$($winrm.Status)" $(if ($winrm.Status -ne 'Running') { 'warn' } else { 'info' })
        if ($winrm.Status -ne 'Running') { Write-Log "WinRM is $($winrm.Status)." 'WARN' }
    } catch {
        Add-Finding 'WinRM' 'Service' 'not found' 'warn'
    }

    $wuKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
    $auKey = Join-Path $wuKey 'AU'
    if (Test-Path $wuKey) {
        $wu = Get-ItemProperty $wuKey
        foreach ($n in @('WUServer', 'WUStatusServer')) {
            if ($wu.PSObject.Properties[$n]) { Add-Finding 'WindowsUpdate' $n ([string]$wu.$n) }
        }
    }
    if (Test-Path $auKey) {
        $au = Get-ItemProperty $auKey
        foreach ($n in @('NoAutoUpdate', 'UseWUServer')) {
            if ($au.PSObject.Properties[$n]) {
                $sev = $(if ($n -eq 'NoAutoUpdate' -and $au.$n -eq 1) { 'warn' } else { 'info' })
                Add-Finding 'WindowsUpdate' $n ([string]$au.$n) $sev
                if ($sev -eq 'warn') {
                    Write-Log 'NoAutoUpdate=1: Windows never refreshes its update cache, so an offline scan reports 0.' 'WARN'
                }
            }
        }
    }
    if (-not (Test-Path $wuKey) -and -not (Test-Path $auKey)) {
        Add-Finding 'WindowsUpdate' 'Policy' 'no policy keys'
    }
}

function Start-NodeAgent {
    Write-Log 'STEP 7 - start' 'STEP'

    if ($Role -ne 'Agent') { Write-Log 'Start is Agent-only; skipped.' 'INFO'; return }
    if (-not $StartAfter) { Write-Log '-StartAfter not supplied; skipped.' 'INFO'; return }
    if (-not $Apply) { Write-Log 'Would start the agent and wait for port 5200.' 'PLAN'; return }

    Invoke-Native -Exe 'schtasks' -Arguments @('/Run', '/TN', 'TestAgentGrpc Interactive') | Out-Null
    Add-Action 'Start' 'scheduled task TestAgentGrpc Interactive'

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-NetTCPConnection -LocalPort 5200 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) {
            Write-Log 'Agent is listening on 5200.' 'OK'
            return
        }
        Start-Sleep -Seconds 2
    }
    Write-Log 'Agent did not start listening on 5200 within 60s.' 'WARN'
}

function Write-NodeReport {
    param([string]$Root, [int]$ExitCode)

    Write-Log 'STEP 8 - report' 'STEP'
    if (-not (Test-Path $ReportFolder)) { New-Item -ItemType Directory -Path $ReportFolder -Force | Out-Null }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $base = "{0}_{1}_{2}" -f $env:COMPUTERNAME, $Role, $stamp
    $jsonPath = Join-Path $ReportFolder "$base.json"
    $htmlPath = Join-Path $ReportFolder "$base.html"

    $freedBytes = Get-TotalBytes -Items $script:Deletions

    $summary = [ordered]@{
        Host = $env:COMPUTERNAME
        Role = $Role
        Mode = $(if ($Apply) { 'APPLY' } else { 'DRY RUN' })
        InstallRoot = $Root
        GeneratedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Operator = "$env:USERDOMAIN\$env:USERNAME"
        LogRetentionDays = $LogRetentionDays
        DumpRetentionDays = $DumpRetentionDays
        BackupKeep = $BackupKeep
        ExitCode = $ExitCode
        SpaceFreedMB = [math]::Round($freedBytes / 1MB, 2)
        Deletions = $script:Deletions
        KeptCrashDumps = $script:KeptDumps
        Actions = $script:Actions
        ConfigChanges = $script:ConfigChanges
        VersionDifferences = $script:VersionDiff
        Health = $script:Findings
        Warnings = $script:Warnings
        Failures = $script:Failures
        Log = $script:Log
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content $jsonPath -Encoding UTF8

    # NOT named H: aliases beat functions in command resolution, and 'h' is the built-in alias for
    # Get-History, whose -Id is an Int64 - so every call failed with a parameter binding error.
    function Get-HtmlSafe { param([string]$s) if ($null -eq $s) { '' } else { [System.Net.WebUtility]::HtmlEncode([string]$s) } }

    $delRows = ''
    foreach ($d in $script:Deletions) {
        $delRows += "<tr><td>$(Get-HtmlSafe $d.Category)</td><td class='mono'>$(Get-HtmlSafe $d.Path)</td><td class='num'>$([math]::Round($d.Bytes / 1KB, 1))</td><td>$(Get-HtmlSafe $d.Modified)</td></tr>"
    }
    if (-not $delRows) { $delRows = "<tr><td colspan='4' class='dim'>nothing selected</td></tr>" }

    $catRows = ''
    foreach ($g in @($script:Deletions | Group-Object Category)) {
        $b = Get-TotalBytes -Items $g.Group
        $catRows += "<tr><td>$(Get-HtmlSafe $g.Name)</td><td class='num'>$($g.Count)</td><td class='num'>$([math]::Round($b / 1MB, 2))</td></tr>"
    }
    if (-not $catRows) { $catRows = "<tr><td colspan='3' class='dim'>none</td></tr>" }

    $keptRows = ''
    foreach ($d in $script:KeptDumps) {
        $keptRows += "<tr><td class='mono'>$(Get-HtmlSafe $d.Path)</td><td class='num'>$([math]::Round($d.Bytes / 1MB, 1))</td><td>$(Get-HtmlSafe $d.Modified)</td><td class='num'>$($d.AgeDays)</td></tr>"
    }
    if (-not $keptRows) { $keptRows = "<tr><td colspan='4' class='dim'>no recent crash dumps</td></tr>" }

    $verRows = ''
    foreach ($v in $script:VersionDiff) {
        $verRows += "<tr><td>$(Get-HtmlSafe $v.Status)</td><td class='mono'>$(Get-HtmlSafe $v.Path)</td></tr>"
    }
    if (-not $verRows) { $verRows = "<tr><td colspan='2' class='dim'>no differences (or no manifest supplied)</td></tr>" }

    $cfgRows = ''
    foreach ($c in $script:ConfigChanges) {
        $cfgRows += "<tr><td class='mono'>$(Get-HtmlSafe $c.Key)</td><td class='mono'>$(Get-HtmlSafe $c.Old)</td><td class='mono'>$(Get-HtmlSafe $c.New)</td></tr>"
    }
    if (-not $cfgRows) { $cfgRows = "<tr><td colspan='3' class='dim'>no changes</td></tr>" }

    $healthRows = ''
    foreach ($f in $script:Findings) {
        $cls = $(if ($f.Severity -eq 'warn') { " class='warnrow'" } else { '' })
        $healthRows += "<tr$cls><td>$(Get-HtmlSafe $f.Area)</td><td>$(Get-HtmlSafe $f.Name)</td><td class='mono'>$(Get-HtmlSafe $f.Value)</td></tr>"
    }
    if (-not $healthRows) { $healthRows = "<tr><td colspan='3' class='dim'>none</td></tr>" }

    $warnRows = ''
    foreach ($w in $script:Warnings) { $warnRows += "<li>$(Get-HtmlSafe $w)</li>" }
    foreach ($w in $script:Failures) { $warnRows += "<li><b>FAILED:</b> $(Get-HtmlSafe $w)</li>" }
    if (-not $warnRows) { $warnRows = "<li class='dim'>none</li>" }

    $modeNote = $(if ($Apply) { 'Changes were applied.' } else { 'DRY RUN - nothing was changed. Every row below is what WOULD happen.' })

    $html = @"
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Node cleanup $base</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;background:#ffffff;color:#323130;margin:0;padding:24px}
 h1{font-size:20px;margin:0 0 4px} h2{font-size:14px;margin:22px 0 8px;color:#605e5c;text-transform:uppercase;letter-spacing:1px}
 .sub{color:#605e5c;font-size:12px;margin-bottom:16px}
 .note{background:#fff4ce;border:1px solid #edebe9;border-radius:4px;padding:8px 12px;font-size:13px;margin-bottom:16px}
 table{border-collapse:collapse;width:100%;font-size:12px;margin-bottom:8px}
 th{background:#f3f2f1;color:#605e5c;text-align:left;padding:7px 10px;border-bottom:1px solid #edebe9;font-size:11px;text-transform:uppercase}
 td{padding:7px 10px;border-bottom:1px solid #edebe9;vertical-align:top}
 .num{text-align:right} .mono{font-family:Consolas,monospace;font-size:11px}
 .dim{color:#a19f9d} .warnrow{background:#fff4ce}
 ul{font-size:12px;margin:0 0 8px 18px;padding:0}
</style></head><body>
<h1>Node cleanup report</h1>
<div class="sub">$(Get-HtmlSafe $env:COMPUTERNAME) &middot; role <b>$(Get-HtmlSafe $Role)</b> &middot; $(Get-HtmlSafe $summary.Mode) &middot; $(Get-HtmlSafe $summary.Operator) &middot; exit code $ExitCode</div>
<div class="note">$(Get-HtmlSafe $modeNote)</div>

<h2>Summary</h2>
<table>
<tr><th>Item</th><th>Value</th></tr>
<tr><td>Install root</td><td class="mono">$(Get-HtmlSafe $Root)</td></tr>
<tr><td>Log retention</td><td>$LogRetentionDays days</td></tr>
<tr><td>Crash dump retention</td><td>$DumpRetentionDays days</td></tr>
<tr><td>Backup sets kept</td><td>$BackupKeep</td></tr>
<tr><td>Files selected for deletion</td><td>$($script:Deletions.Count)</td></tr>
<tr><td>Space freed</td><td>$($summary.SpaceFreedMB) MB</td></tr>
<tr><td>Warnings</td><td>$($script:Warnings.Count)</td></tr>
</table>

<h2>Space freed by category</h2>
<table><tr><th>Category</th><th>Items</th><th>MB</th></tr>$catRows</table>

<h2>Files $(if ($Apply) { 'deleted' } else { 'that would be deleted' })</h2>
<table><tr><th>Category</th><th>Path</th><th>KB</th><th>Modified</th></tr>$delRows</table>

<h2>Crash dumps kept - recent crash evidence</h2>
<table><tr><th>Path</th><th>MB</th><th>Modified</th><th>Age (days)</th></tr>$keptRows</table>

<h2>Version differences (report only)</h2>
<table><tr><th>Status</th><th>Path</th></tr>$verRows</table>

<h2>Config changes</h2>
<table><tr><th>Key</th><th>Old</th><th>New</th></tr>$cfgRows</table>

<h2>Health findings</h2>
<table><tr><th>Area</th><th>Name</th><th>Value</th></tr>$healthRows</table>

<h2>Warnings</h2>
<ul>$warnRows</ul>

<footer class="dim" style="font-size:11px;margin-top:18px">Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')</footer>
</body></html>
"@
    Set-Content -Path $htmlPath -Value $html -Encoding UTF8

    Write-Log "JSON report: $jsonPath" 'OK'
    Write-Log "HTML report: $htmlPath" 'OK'
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if (-not $InstallRoot) {
    $InstallRoot = $(if ($Role -eq 'Controller') { 'C:\TestControllerService' } else { 'C:\TestAgentService' })
}
$InstallRoot = $InstallRoot.TrimEnd('\')

Write-Host ''
Write-Host '==============================================================' -ForegroundColor Cyan
Write-Host '  TestAgentSolution - node cleanup' -ForegroundColor Cyan
Write-Host '==============================================================' -ForegroundColor Cyan

$exitCode = 0
try {
    $preserve = Get-PreserveLists -ExplicitPath $InventoryPath

    if (-not (Invoke-Precheck -Root $InstallRoot)) {
        $exitCode = 2
    }
    elseif (-not (Stop-NodeProcesses -Root $InstallRoot)) {
        $exitCode = 2
    }
    else {
        Invoke-Clean        -Root $InstallRoot -Preserve $preserve
        Invoke-VersionCheck -Root $InstallRoot -Path $ManifestPath -Preserve $preserve
        Invoke-ConfigPatch  -Root $InstallRoot -Path $ConfigPatchPath
        Invoke-HealthChecks -Root $InstallRoot
        Start-NodeAgent
    }
}
catch {
    Write-Log "Unhandled failure: $($_.Exception.Message)" 'FAIL'
    $exitCode = 2
}

if ($exitCode -eq 0 -and $script:Warnings.Count -gt 0) { $exitCode = 1 }
if ($script:Failures.Count -gt 0) { $exitCode = 2 }

try { Write-NodeReport -Root $InstallRoot -ExitCode $exitCode }
catch { Write-Log "Report generation failed: $($_.Exception.Message)" 'WARN' }

Write-Host ''
$verdict = switch ($exitCode) { 0 { 'OK' } 1 { 'COMPLETED WITH WARNINGS' } default { 'FAILED' } }
$colour  = switch ($exitCode) { 0 { 'Green' } 1 { 'Yellow' } default { 'Red' } }
Write-Host "RESULT: $verdict (exit $exitCode)" -ForegroundColor $colour
exit $exitCode
