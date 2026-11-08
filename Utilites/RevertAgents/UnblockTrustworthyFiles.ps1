<#
.SYNOPSIS
    Unblocks (removes the downloaded-from-internet mark from) every file under a folder
    copied from an external location.

.DESCRIPTION
    Original author: Siva Thota - AppServer.
    Fails if the folder is missing instead of falling back to C:\ (the old behaviour
    unblocked the whole drive when the argument was wrong or omitted).

.PARAMETER Path
    Folder to process, recursively (position 0, so the old "script.ps1 <folder>" call still works).

.EXAMPLE
    .\UnblockTrustworthyFiles.ps1 C:\TestSetup\RevertAgents

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File UnblockTrustworthyFiles.ps1 -Path C:\TestSetup
#>
param(
    [Parameter(Mandatory, Position = 0)][string]$Path
)

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Host "[FAIL] Folder not found: $Path" -ForegroundColor Red
    exit 1
}
Write-Host (Resolve-Path -LiteralPath $Path).Path
Get-ChildItem -LiteralPath $Path -File -Recurse | Unblock-File