param(
    [string]$Source = "dist",
    [string]$Destination = "..\\TestController.WebApi\\wwwroot"
)

$ErrorActionPreference = "Stop"

$sourcePath = Join-Path -Path (Get-Location) -ChildPath $Source
$destinationPath = Join-Path -Path (Get-Location) -ChildPath $Destination

if (-not (Test-Path $sourcePath)) {
    throw "Source folder '$sourcePath' does not exist. Run 'npm run build' first."
}

if (-not (Test-Path $destinationPath)) {
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
}

Copy-Item -Path (Join-Path $sourcePath "*") -Destination $destinationPath -Recurse -Force
Write-Host "Copied '$sourcePath' to '$destinationPath'."