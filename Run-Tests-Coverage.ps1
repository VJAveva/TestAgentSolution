<#
.SYNOPSIS
    Runs all tests with code coverage collection and generates an HTML report.

.PARAMETER ReportDir
    Output directory for coverage reports. Default: TestResults

.EXAMPLE
    .\Run-Tests-Coverage.ps1
    .\Run-Tests-Coverage.ps1 -ReportDir "C:\Reports"
#>
param(
    [string]$ReportDir = "TestResults"
)

$ErrorActionPreference = "Stop"
$SolutionRoot = $PSScriptRoot
if (-not $SolutionRoot) { $SolutionRoot = (Get-Location).Path }

# Clean previous results
if (Test-Path $ReportDir) { Remove-Item $ReportDir -Recurse -Force }

Write-Host "Running tests with coverage..." -ForegroundColor Cyan

dotnet test "$SolutionRoot\TestAgentSolution.sln" `
    --collect:"XPlat Code Coverage" `
    --results-directory "$ReportDir" `
    --settings "$SolutionRoot\coverlet.runsettings" `
    --verbosity normal

if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
    Write-Host "Test run failed with exit code $LASTEXITCODE" -ForegroundColor Red
}

# Check if reportgenerator is installed
$rg = Get-Command reportgenerator -ErrorAction SilentlyContinue
if (-not $rg) {
    Write-Host "Installing dotnet-reportgenerator..." -ForegroundColor Yellow
    dotnet tool install -g dotnet-reportgenerator-globaltool
}

$coverageFiles = Get-ChildItem -Path $ReportDir -Recurse -Filter "coverage.cobertura.xml"
if ($coverageFiles.Count -gt 0) {
    $reports = ($coverageFiles | ForEach-Object { $_.FullName }) -join ";"
    reportgenerator `
        -reports:$reports `
        -targetdir:"$ReportDir\html" `
        -reporttypes:"Html;TextSummary"

    $summary = Get-Content "$ReportDir\html\Summary.txt" -ErrorAction SilentlyContinue
    if ($summary) {
        Write-Host "`n=== Coverage Summary ===" -ForegroundColor Green
        $summary | Write-Host
    }

    Write-Host "`nHTML report: $ReportDir\html\index.html" -ForegroundColor Cyan
} else {
    Write-Host "No coverage files found." -ForegroundColor Yellow
}
