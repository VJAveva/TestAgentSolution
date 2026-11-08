<#
.SYNOPSIS
    Checks every relative link in tracked .md and .html files resolves to a file or folder in the repo.
.DESCRIPTION
    Errors: target missing, or its case differs from the tracked path (fine on Windows, broken on case-sensitive hosts).
    Warnings: target exists on disk but is not tracked by git (e.g. a git-ignored folder), so the link breaks for others.
    Scans *.md everywhere and *.html under docs/ (app HTML such as the Vite index.html is not documentation).
    Fenced code blocks, external links (http, https, mailto, #anchor-only) and template placeholders (${...}) are skipped.
.EXAMPLE
    .\tools\Check-DocLinks.ps1            # exit 0 = no broken links
    .\tools\Check-DocLinks.ps1 -Quiet     # summary only
#>
param(
    [string] $Root = (Split-Path $PSScriptRoot -Parent),
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Push-Location $Root
try {
    $tracked = @(git ls-files)
    $trackedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $trackedLower = @{}
    $trackedDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($t in $tracked) {
        [void]$trackedSet.Add($t)
        $trackedLower[$t.ToLowerInvariant()] = $t
        $d = $t
        while (($i = $d.LastIndexOf('/')) -gt 0) { $d = $d.Substring(0, $i); [void]$trackedDirs.Add($d) }
    }

    $mdLink = [regex]'!?\[[^\]]*\]\(\s*(<[^>]+>|[^)\s]+)(?:\s+"[^"]*")?\s*\)'
    $htmlLink = [regex]'(?:href|src)\s*=\s*"([^"]+)"'
    $errors = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $checked = 0

    foreach ($file in $tracked | Where-Object { ($_ -match '\.md$' -or $_ -match '^docs/.*\.html$') -and $_ -notmatch '(^|/)(node_modules|dist|wwwroot|publish)/' }) {
        $isMd = $file.EndsWith('.md')
        $dir = if ($file.Contains('/')) { $file.Substring(0, $file.LastIndexOf('/')) } else { '' }
        $inFence = $false
        $lineNo = 0
        foreach ($line in [IO.File]::ReadLines((Join-Path $Root $file))) {
            $lineNo++
            if ($isMd -and $line.TrimStart().StartsWith('```')) { $inFence = -not $inFence; continue }
            if ($inFence) { continue }

            $found = if ($isMd) { $mdLink.Matches($line) } else { $htmlLink.Matches($line) }
            foreach ($m in $found) {
                $target = $m.Groups[1].Value.Trim('<', '>')
                if ($target -match '^(https?:|mailto:|data:|javascript:|#|\{)' -or $target -eq '' -or $target.Contains('${')) { continue }
                $target = ($target -split '[#?]')[0]
                if ($target -eq '') { continue }
                $target = [Uri]::UnescapeDataString($target).Replace('\', '/')

                $joined = if ($target.StartsWith('/')) { $target.TrimStart('/') } elseif ($dir) { "$dir/$target" } else { $target }
                $parts = [System.Collections.Generic.List[string]]::new()
                foreach ($p in $joined -split '/') {
                    if ($p -eq '' -or $p -eq '.') { continue }
                    if ($p -eq '..') { if ($parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) }; continue }
                    $parts.Add($p)
                }
                $resolved = $parts -join '/'
                $checked++
                $where = "${file}:$lineNo -> $target"

                if ($resolved -eq '' -or $trackedSet.Contains($resolved) -or $trackedDirs.Contains($resolved)) { continue }
                if ($trackedLower.ContainsKey($resolved.ToLowerInvariant())) {
                    $errors.Add("$where  (case differs: $($trackedLower[$resolved.ToLowerInvariant()]))"); continue
                }
                if (Test-Path -LiteralPath (Join-Path $Root $resolved)) { $warnings.Add("$where  (exists but is not tracked)"); continue }
                $errors.Add("$where  (not found)")
            }
        }
    }

    if (-not $Quiet) {
        $errors | ForEach-Object { "ERROR  $_" }
        $warnings | ForEach-Object { "WARN   $_" }
    }
    "Checked $checked link(s): $($errors.Count) broken, $($warnings.Count) warning(s)."
    exit ([int]($errors.Count -gt 0))
}
finally {
    Pop-Location
}
