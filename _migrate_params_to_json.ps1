<#
    Builds pipeline-config.json from the existing *Variables.txt files.

    Keys identical in every source file become "global"; anything that differs or is unique
    becomes that stage's "profile". Values are never printed - only key names - so the
    vCloud password moves across without being displayed.

    Behaviour-preserving: "pipelines" is left empty so every pipeline resolves the same
    build it resolves today. Pin per-pipeline builds afterwards, from the UI.
#>
param(
    [string]$Dir = 'C:\TestControllerService\Parameters\SP2023R2SP2',
    [string[]]$Sources = @('WarmVariables.txt', 'SanityVariables.txt'),
    [string[]]$Profiles = @('Warm', 'Sanity'),
    [string[]]$PipelineTags = @('Revert 9 Nodes - Install SP2023R2SP2'),
    [string]$OutFile = 'pipeline-config.json',
    [switch]$Apply
)

function Read-Params {
    param([string]$Path)
    $map = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line.TrimStart().StartsWith('#')) { continue }
        $i = $line.IndexOf(',')
        if ($i -gt 0) {
            $map[$line.Substring(0, $i).Trim()] = $line.Substring($i + 1).Trim()
        }
    }
    return $map
}

$parsed = @{}
for ($n = 0; $n -lt $Sources.Count; $n++) {
    $path = Join-Path $Dir $Sources[$n]
    if (-not (Test-Path -LiteralPath $path)) { throw "Source not found: $path" }
    $parsed[$Profiles[$n]] = Read-Params $path
}

# A key is global only when every source agrees on its value.
# @() is load-bearing: without it a single string gets indexed per-character ($v[0] -> 'O')
# and .Count on a lone hashtable returns its entry count rather than 1.
$allKeys = @($parsed.Values | ForEach-Object { $_.Keys } | Select-Object -Unique)
$global = [ordered]@{}
foreach ($k in $allKeys) {
    $present = @($parsed.Values | Where-Object { $_.Contains($k) })
    if ($present.Count -ne $parsed.Count) { continue }
    $values = @($parsed.Values | ForEach-Object { $_[$k] } | Select-Object -Unique)
    if ($values.Count -eq 1) { $global[$k] = $values[0] }
}

$profileMaps = [ordered]@{}
foreach ($name in $Profiles) {
    $only = [ordered]@{}
    foreach ($k in $parsed[$name].Keys) {
        if (-not $global.Contains($k)) { $only[$k] = $parsed[$name][$k] }
    }
    $profileMaps[$name] = $only
}

$pipelines = [ordered]@{}
foreach ($tag in $PipelineTags) { $pipelines[$tag] = [ordered]@{} }

$config = [ordered]@{
    version   = 1
    global    = $global
    profiles  = $profileMaps
    pipelines = $pipelines
}

$json = $config | ConvertTo-Json -Depth 8
$target = Join-Path $Dir $OutFile

Write-Output "source files   : $($Sources -join ', ')"
Write-Output "global keys    : $($global.Keys.Count)  [$(($global.Keys) -join ', ')]"
foreach ($name in $Profiles) {
    Write-Output "profile $name : $($profileMaps[$name].Keys.Count)  [$(($profileMaps[$name].Keys) -join ', ')]"
}
Write-Output "pipelines      : $($pipelines.Keys.Count) (empty overrides - behaviour preserved)"
Write-Output "target         : $target"

if (-not $Apply) {
    Write-Output ''
    Write-Output 'DRY RUN - nothing written. Re-run with -Apply.'
    return
}

if (Test-Path -LiteralPath $target) {
    $backup = "$target.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item -LiteralPath $target -Destination $backup -Force
    Write-Output "backed up existing to $(Split-Path $backup -Leaf)"
}

Set-Content -LiteralPath $target -Value $json -Encoding UTF8
Write-Output "WROTE $target ($((Get-Item -LiteralPath $target).Length) bytes)"
