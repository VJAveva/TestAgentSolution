<#
.SYNOPSIS
    Checks that docs are in line with the code.
.DESCRIPTION
    Errors (exit 1):
      - broken relative links (runs tools/Check-DocLinks.ps1)
      - a docs/features/<X>/ folder without README.md, or a README without front matter
      - a README missing a required front-matter key (feature, status, owner, code, tests, last-verified)
      - a `code:` or `tests:` path that does not exist
      - a last-verified value that is not a yyyy-MM-dd date
    Warnings (exit 0 unless -Strict):
      - code under a feature's `code:` paths was committed after its last-verified date (git log)
      - owner is still TODO, or `feature:` differs from the folder name
      - a "TODO verify" left in a feature README
    The staleness check needs full git history; in CI check out with fetch-depth: 0.
.EXAMPLE
    .\tools\Check-Docs.ps1             # errors fail, warnings only reported
    .\tools\Check-Docs.ps1 -Strict     # warnings fail too (use this to make CI blocking)
#>
param(
    [string] $Root = (Split-Path $PSScriptRoot -Parent),
    [switch] $Strict
)

$ErrorActionPreference = 'Stop'
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$inCi = $env:GITHUB_ACTIONS -eq 'true'

Push-Location $Root
try {
    # 1. Links
    "== Links"
    $linkOut = & (Join-Path $PSScriptRoot 'Check-DocLinks.ps1') -Root $Root
    $linkExit = $LASTEXITCODE
    $linkOut | Where-Object { $_ -notmatch '^(ERROR|WARN)' }
    foreach ($l in $linkOut) {
        if ($l -match '^ERROR\s+(.*)') { $errors.Add("link: $($Matches[1])") }
        elseif ($l -match '^WARN\s+(.*)') { $warnings.Add("link: $($Matches[1])") }
    }
    if ($linkExit -ne 0 -and -not ($linkOut -match '^ERROR')) { $errors.Add("link check exited $linkExit") }

    # 2. Feature READMEs
    "== Feature READMEs"
    $required = 'feature', 'status', 'owner', 'code', 'tests', 'last-verified'
    $features = Get-ChildItem -Directory (Join-Path $Root 'docs/features') | Where-Object { $_.Name -ne '_template' }
    $staleCount = 0
    foreach ($dir in $features) {
        $rel = "docs/features/$($dir.Name)/README.md"
        $readme = Join-Path $dir.FullName 'README.md'
        if (-not (Test-Path -LiteralPath $readme)) { $errors.Add("${rel}: missing"); continue }

        $lines = [IO.File]::ReadAllLines($readme)
        if ($lines.Count -lt 3 -or $lines[0].Trim() -ne '---') { $errors.Add("${rel}: no front matter"); continue }
        $end = [Array]::IndexOf(($lines | ForEach-Object { $_.Trim() }), '---', 1)
        if ($end -lt 0) { $errors.Add("${rel}: front matter not closed"); continue }

        $fm = @{}
        foreach ($l in $lines[1..($end - 1)]) {
            if ($l -match '^\s*([\w-]+)\s*:\s*(.*?)\s*(#.*)?$') { $fm[$Matches[1]] = $Matches[2] }
        }
        foreach ($k in $required) { if (-not $fm.ContainsKey($k)) { $errors.Add("${rel}: front matter has no '$k'") } }

        if ($fm['feature'] -and $fm['feature'] -ne $dir.Name) { $warnings.Add("${rel}: feature '$($fm['feature'])' differs from folder '$($dir.Name)'") }
        if ($fm['owner'] -match '^(TODO)?$') { $warnings.Add("${rel}: owner not set") }
        $todo = @($lines | Select-String -SimpleMatch 'TODO verify').Count
        if ($todo) { $warnings.Add("${rel}: $todo 'TODO verify' item(s)") }

        $paths = @{}
        foreach ($k in 'code', 'tests') {
            $paths[$k] = @(($fm[$k] -replace '^\[|\]$', '') -split ',' | ForEach-Object { $_.Trim().Trim('"', "'") } | Where-Object { $_ })
            foreach ($p in $paths[$k]) {
                if (-not (Test-Path -LiteralPath (Join-Path $Root $p))) { $errors.Add("${rel}: ${k} path not found: $p") }
            }
        }

        $verified = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($fm['last-verified'], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$verified)) {
            if ($fm.ContainsKey('last-verified')) { $errors.Add("${rel}: last-verified '$($fm['last-verified'])' is not yyyy-MM-dd") }
            continue
        }

        $existing = @($paths['code'] | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) })
        if ($existing.Count -eq 0) { continue }
        $since = $verified.AddDays(1).ToString('yyyy-MM-dd')
        $commits = @(git log --since="$since 00:00" --format='%h %cs %s' -- $existing)
        if ($commits.Count -gt 0) {
            $staleCount++
            $warnings.Add("${rel}: code changed after last-verified $($fm['last-verified']) - $($commits.Count) commit(s), latest: $($commits[0])")
        }
    }
    "Checked $($features.Count) feature folder(s); $staleCount with code changes after last-verified."

    $errors | ForEach-Object { "ERROR  $_"; if ($inCi) { "::error::$_" } }
    $warnings | ForEach-Object { "WARN   $_"; if ($inCi) { "::warning::$_" } }
    "check-docs: $($errors.Count) error(s), $($warnings.Count) warning(s)."

    if ($errors.Count -gt 0) { exit 1 }
    if ($Strict -and $warnings.Count -gt 0) { exit 1 }
    exit 0
}
finally {
    Pop-Location
}
