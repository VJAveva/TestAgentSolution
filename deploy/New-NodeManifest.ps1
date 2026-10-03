<#
.SYNOPSIS
    Builds a node manifest (relative path, file version, SHA256) from a publish folder.

.DESCRIPTION
    Invoke-NodeCleanup.ps1 compares a node against this manifest and reports Missing / Extra /
    Different. It never copies files - deployment does that.

    Host-local STATE is excluded by default, using the same preserveFiles patterns the fleet
    deployment protects. Without that exclusion every node would report appsettings.json and the
    databases as "Different", because those legitimately differ per node.

.PARAMETER PublishFolder
    Build output to describe, e.g. publish\agent.

.PARAMETER OutFile
    JSON manifest to write.

.PARAMETER Exclude
    Leaf-name patterns to omit. Defaults to the inventory's preserveFiles.

.PARAMETER InventoryPath
    fleet-inventory.json supplying the default excludes. Auto-detected next to this script.

.EXAMPLE
    .\New-NodeManifest.ps1 -PublishFolder ..\publish\agent -OutFile .\agent-manifest.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishFolder,

    [Parameter(Mandatory = $true)]
    [string]$OutFile,

    [string[]]$Exclude,
    [string]$InventoryPath
)

$ErrorActionPreference = 'Stop'

# Same fallback as Invoke-NodeCleanup.ps1; the Pester tests pin both to the inventory.
# <PreserveList:BEGIN>
$fallbackPreserveFiles = @(
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
# <PreserveList:END>

function Get-DefaultExcludes {
    param([string]$ExplicitPath)

    $candidates = @()
    if ($ExplicitPath) { $candidates += $ExplicitPath }
    $candidates += (Join-Path $PSScriptRoot 'fleet-inventory.json')
    $candidates += (Join-Path $PSScriptRoot 'deploy\fleet-inventory.json')

    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        try {
            $inv = Get-Content $c -Raw | ConvertFrom-Json
            $files = @($inv.preserveFiles)
            if ($files.Count -gt 0) {
                Write-Host "Excludes from $c ($($files.Count) patterns)." -ForegroundColor Green
                return $files
            }
        } catch { }
    }
    Write-Host 'Excludes from the embedded fallback list.' -ForegroundColor Yellow
    return $fallbackPreserveFiles
}

function Test-Excluded {
    param([string]$Name, [string[]]$Patterns)
    foreach ($p in $Patterns) {
        if ($Name -like $p) { return $true }
    }
    return $false
}

if (-not (Test-Path $PublishFolder)) { throw "Publish folder not found: $PublishFolder" }
$root = (Resolve-Path $PublishFolder).Path.TrimEnd('\')

if (-not $Exclude -or $Exclude.Count -eq 0) { $Exclude = Get-DefaultExcludes -ExplicitPath $InventoryPath }

Write-Host "Scanning $root ..." -ForegroundColor Cyan
$files = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue)

$entries = New-Object System.Collections.Generic.List[object]
$skipped = 0
foreach ($f in $files) {
    if (Test-Excluded -Name $f.Name -Patterns $Exclude) { $skipped++; continue }

    $ver = ''
    try {
        $vi = $f.VersionInfo
        if ($vi -and $vi.FileVersion) { $ver = $vi.FileVersion }
    } catch { }

    $entries.Add([pscustomobject]@{
        Path        = $f.FullName.Substring($root.Length + 1)
        FileVersion = $ver
        Sha256      = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        Bytes       = $f.Length
    })
}

$manifest = [ordered]@{
    GeneratedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    SourceFolder  = $root
    CreatedBy     = "$env:USERDOMAIN\$env:USERNAME on $env:COMPUTERNAME"
    ExcludedCount = $skipped
    FileCount     = $entries.Count
    Files         = @($entries | Sort-Object Path)
}

$outDir = Split-Path $OutFile -Parent
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$manifest | ConvertTo-Json -Depth 6 | Set-Content $OutFile -Encoding UTF8

Write-Host ("Manifest written: {0}" -f $OutFile) -ForegroundColor Green
Write-Host ("  {0} file(s) described, {1} excluded as host state." -f $entries.Count, $skipped)
