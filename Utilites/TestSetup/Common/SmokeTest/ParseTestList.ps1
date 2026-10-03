<#
.SYNOPSIS
    Reads TestCaseList.json and outputs a pipe-delimited flat file
    for the batch execution engine.

.DESCRIPTION
    Converts JSON test definitions into simple lines:
      SEQ|ACTION|SET_KEY|DLL_KEY|TEST_NAME|EXTRA

    Fields:
      SEQ       - Global sequence number (1..N)
      ACTION    - header, run, skip, wait, service
      SET_KEY   - Set1, Set2, Set3 (used for subfolder routing)
      DLL_KEY   - primary, script, or empty
      TEST_NAME - MSTest method name or display name for headers
      EXTRA     - skip_reason, wait_seconds, service_action, or empty

.PARAMETER JsonPath
    Path to TestCaseList.json

.PARAMETER SetName
    Which set to extract: Set1, Set2, Set3, or All

.PARAMETER OutPath
    Output file path for the flat list
#>
param(
    [Parameter(Mandatory=$true)] [string]$JsonPath,
    [Parameter(Mandatory=$true)] [string]$SetName,
    [Parameter(Mandatory=$true)] [string]$OutPath
)

if (-not (Test-Path $JsonPath)) {
    Write-Error "JSON file not found: $JsonPath"
    exit 1
}

$json = Get-Content -Raw $JsonPath | ConvertFrom-Json

$setsToProcess = @()
if ($SetName -eq "All") {
    $setsToProcess = @("Set1", "Set2", "Set3")
} else {
    if (-not $json.PSObject.Properties.Name.Contains($SetName)) {
        Write-Error "Invalid set: $SetName. Valid: Set1, Set2, Set3, All"
        exit 1
    }
    $setsToProcess = @($SetName)
}

$lines = @()
$globalSeq = 0

foreach ($set in $setsToProcess) {
    $setData = $json.$set
    $displayName = $setData.name

    # Header line: SEQ|header|SET_KEY|_|DISPLAY_NAME|
    $globalSeq++
    $lines += "$globalSeq|header|$set|_|$displayName|"

    foreach ($tc in $setData.tests) {
        $globalSeq++
        $action   = $tc.action
        $dllKey   = if ($tc.dll) { $tc.dll } else { "_" }
        $testName = if ($tc.test) { $tc.test } else { "_" }
        $extra    = ""

        switch ($action) {
            "skip"    { $extra = $tc.skip_reason }
            "wait"    { $extra = $tc.wait_seconds }
            "service" { $extra = $tc.service_action }
        }

        $lines += "$globalSeq|$action|$set|$dllKey|$testName|$extra"
    }
}

$totalLines = $lines.Count
$output = @("TOTAL|$totalLines")
$output += $lines
$output | Out-File -FilePath $OutPath -Encoding ASCII
Write-Host "[INFO] Exported $totalLines entries for $SetName to $OutPath"
exit 0
