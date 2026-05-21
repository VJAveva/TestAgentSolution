<#
.SYNOPSIS
    Builds and publishes all TestAgentSolution projects in one step.

.DESCRIPTION
    Professional build pipeline that:
      1. Validates prerequisites with auto-repair
      2. Restores NuGet packages
      3. Builds all .NET projects
      4. Runs tests with detailed reporting
      5. Publishes all 4 .NET projects
      6. Builds the React WebClient
      7. Copies all artifacts to C:\Deployment
      8. Generates a deployment manifest

.PARAMETER SolutionRoot
    Path to the solution root. Default: script location.

.PARAMETER DeployRoot
    Root deployment folder. Default: C:\Deployment

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER Runtime
    Target runtime. Default: win-x64

.PARAMETER SelfContained
    Publish as self-contained. Default: true

.PARAMETER SingleFile
    Publish as single-file executable. Default: true

.PARAMETER WebApiUrl
    API URL baked into the React build. Default: http://jvgr22:8080

.PARAMETER SkipTests
    Skip running unit tests.

.PARAMETER SkipWebClient
    Skip building the React WebClient.

.PARAMETER SkipClean
    Skip cleaning output folders before publish.

.EXAMPLE
    .\Publish-All.ps1
    .\Publish-All.ps1 -SkipTests -WebApiUrl "http://controller01:8080"
    .\Publish-All.ps1 -SelfContained $false -SingleFile $false
#>

[CmdletBinding()]
param(
    [string]$SolutionRoot = "",
    [string]$DeployRoot = "C:\Deployment",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [bool]$SingleFile = $true,
    [string]$WebApiUrl = "http://jvgr22:8080",
    [switch]$SkipTests,
    [switch]$SkipWebClient,
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date
$script:stepStart = Get-Date
$script:stepNumber = 0
$script:totalSteps = 8
$script:errors = @()
$script:warnings = @()

# ── Resolve solution root ──
if (-not $SolutionRoot) {
    $SolutionRoot = $PSScriptRoot
    if (-not $SolutionRoot) { $SolutionRoot = (Get-Location).Path }
}
$SolutionRoot = (Resolve-Path $SolutionRoot -ErrorAction Stop).Path

# ── Project definitions ──
$dotnetProjects = @(
    @{ Name = "TestControllerGrpc";    Csproj = "TestControllerGrpc\TestControllerGrpc.csproj";       Deploy = "TestControllerGrpc" }
    @{ Name = "TestAgentGrpc";         Csproj = "TestAgentGrpc\TestAgentGrpc.csproj";                 Deploy = "TestAgentGrpc" }
    @{ Name = "TestAgentDisplay";      Csproj = "TestAgentDisplay\TestAgentDisplay.csproj";           Deploy = "TestAgentDisplay" }
    @{ Name = "TestController.WebApi"; Csproj = "TestController.WebApi\TestController.WebApi.csproj"; Deploy = "TestController.WebApi" }
)

$testProjects = @(
    "TestControllerGrpc.Tests\TestControllerGrpc.Tests.csproj"
    "TestController.WebApi.Tests\TestController.WebApi.Tests.csproj"
)

$webClientFolder = "TestController.WebClient"

# ══════════════════════════════════════════════════════
# LOGGING
# ══════════════════════════════════════════════════════
function Log {
    param([string]$Msg, [string]$Level = "INFO")
    $ts = Get-Date -Format "HH:mm:ss"
    $color = switch ($Level) {
        "OK"   { "Green" }
        "WARN" { "Yellow" }
        "FAIL" { "Red" }
        "STEP" { "Cyan" }
        "SKIP" { "DarkGray" }
        default { "White" }
    }
    $tag = switch ($Level) {
        "OK"   { "PASS" }
        "FAIL" { "FAIL" }
        "WARN" { "WARN" }
        "STEP" { "STEP" }
        "SKIP" { "SKIP" }
        default { "INFO" }
    }
    Write-Host "[$ts] [$tag] $Msg" -ForegroundColor $color

    if ($Level -eq "WARN") { $script:warnings += $Msg }
    if ($Level -eq "FAIL") { $script:errors += $Msg }
}

function FailExit {
    param([string]$Msg)
    Log $Msg "FAIL"
    $elapsed = (Get-Date) - $startTime
    $elapsedStr = $elapsed.ToString('mm\:ss')
    Log "Build FAILED after $elapsedStr" "FAIL"
    exit 1
}

function StepHeader {
    param([string]$Title)
    $script:stepNumber++
    $script:stepStart = Get-Date
    Write-Host ""
    Log "STEP $($script:stepNumber)/$($script:totalSteps): $Title" "STEP"
}

function StepComplete {
    param([string]$Detail = "")
    $elapsed = (Get-Date) - $script:stepStart
    $elapsedStr = $elapsed.ToString('mm\:ss')
    $msg = "Completed in $elapsedStr"
    if ($Detail) { $msg = "$Detail -- $elapsedStr" }
    Log $msg "OK"
}

function SafeRemoveDir {
    param([string]$Path)
    if (Test-Path $Path) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            # Files may be locked — try with retries
            for ($i = 1; $i -le 3; $i++) {
                Start-Sleep -Seconds 2
                try {
                    Remove-Item $Path -Recurse -Force -ErrorAction Stop
                    return $true
                }
                catch {
                    if ($i -eq 3) {
                        Log "Cannot remove $Path - files may be locked. Kill any running instances first." "WARN"
                        return $false
                    }
                }
            }
        }
    }
    return $true
}

function RunCommand {
    param(
        [string]$Description,
        [string]$Command,
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    Log "$Description"
    $output = & $Command @Arguments 2>&1

    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            Log "$Description returned exit code $LASTEXITCODE - continuing" "WARN"
            return @{ Success = $false; Output = $output; ExitCode = $LASTEXITCODE }
        }
        else {
            # Show last 15 lines of output for diagnosis
            $output | Select-Object -Last 15 | ForEach-Object {
                Write-Host "    $_" -ForegroundColor DarkGray
            }
            FailExit "$Description failed with exit code $LASTEXITCODE"
        }
    }

    return @{ Success = $true; Output = $output; ExitCode = 0 }
}

function GetFolderStats {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return @{ Files = 0; SizeMB = 0 } }
    $files = Get-ChildItem $Path -Recurse -File -ErrorAction SilentlyContinue
    $count = $files.Count
    $bytes = ($files | Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
    $sizeMB = [math]::Round($bytes / 1MB, 1)
    return @{ Files = $count; SizeMB = $sizeMB }
}

# ══════════════════════════════════════════════════════
# BANNER
# ══════════════════════════════════════════════════════
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "    TestAgentSolution -- Build and Publish Pipeline" -ForegroundColor Cyan
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host ""
Log "Solution:       $SolutionRoot"
Log "Deploy to:      $DeployRoot"
Log "Configuration:  $Configuration | Runtime: $Runtime"
Log "Self-contained: $SelfContained | Single-file: $SingleFile"
Log "WebClient API:  $WebApiUrl"
Log "Skip tests:     $SkipTests | Skip WebClient: $SkipWebClient"
Write-Host ""

# ══════════════════════════════════════════════════════
# STEP 1: Validate prerequisites
# ══════════════════════════════════════════════════════
StepHeader "Validating prerequisites"

# .NET SDK
$dotnetVersion = $null
try { $dotnetVersion = (& dotnet --version 2>$null) } catch {}
if (-not $dotnetVersion) {
    FailExit ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
}
Log ".NET SDK: $dotnetVersion" "OK"

# Check for .NET 10
$sdkList = & dotnet --list-sdks 2>$null
$hasNet10 = $sdkList | Where-Object { $_ -match "^10\." }
if (-not $hasNet10) {
    Log ".NET 10 SDK not found. Current SDKs:" "WARN"
    $sdkList | ForEach-Object { Log "  $_" }
    Log "Projects targeting net10.0 will fail. Install .NET 10 SDK." "WARN"
}
else {
    Log ".NET 10 SDK available" "OK"
}

# Node.js and npm for WebClient
if (-not $SkipWebClient) {
    $nodeVersion = $null
    try { $nodeVersion = (& node --version 2>$null) } catch {}
    if (-not $nodeVersion) {
        Log "Node.js not found. WebClient build will be skipped." "WARN"
        $SkipWebClient = $true
    }
    else {
        Log "Node.js: $nodeVersion" "OK"
        $npmVersion = & npm --version 2>$null
        Log "npm: $npmVersion" "OK"
    }
}

# Solution file
$slnFile = Get-ChildItem $SolutionRoot -Filter "*.sln" -Depth 0 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $slnFile) {
    FailExit "No .sln file found in $SolutionRoot"
}
Log "Solution file: $($slnFile.Name)" "OK"

# Verify project files
$missingProjects = @()
foreach ($proj in $dotnetProjects) {
    $csprojPath = Join-Path $SolutionRoot $proj.Csproj
    if (-not (Test-Path $csprojPath)) {
        $missingProjects += $proj.Name
    }
}
if ($missingProjects.Count -gt 0) {
    FailExit "Missing project files: $($missingProjects -join ', ')"
}
Log "All $($dotnetProjects.Count) .NET project files verified" "OK"

# WebClient package.json
if (-not $SkipWebClient) {
    $wcPath = Join-Path $SolutionRoot $webClientFolder
    $pkgJsonPath = Join-Path $wcPath "package.json"
    if (-not (Test-Path $pkgJsonPath)) {
        Log "WebClient package.json not found at $pkgJsonPath -- skipping WebClient" "WARN"
        $SkipWebClient = $true
    }
    else {
        Log "WebClient package.json found" "OK"
    }
}

# Create deployment root
if (-not (Test-Path $DeployRoot)) {
    New-Item -Path $DeployRoot -ItemType Directory -Force | Out-Null
    Log "Created: $DeployRoot" "OK"
}

StepComplete "Prerequisites validated"

# ══════════════════════════════════════════════════════
# STEP 2: Clean previous output
# ══════════════════════════════════════════════════════
StepHeader "Cleaning previous output"

if (-not $SkipClean) {
    # Kill any running instances that might lock files
    foreach ($proj in $dotnetProjects) {
        $procName = $proj.Name
        $running = Get-Process -Name $procName -ErrorAction SilentlyContinue
        if ($running) {
            Log "Killing running process: $procName (PID $($running.Id -join ', '))" "WARN"
            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
    }

    # Clean deployment folders
    $cleanedCount = 0
    foreach ($proj in $dotnetProjects) {
        $deployPath = Join-Path $DeployRoot $proj.Deploy
        if (SafeRemoveDir $deployPath) { $cleanedCount++ }
    }
    if (-not $SkipWebClient) {
        $wcDeployPath = Join-Path $DeployRoot $webClientFolder
        if (SafeRemoveDir $wcDeployPath) { $cleanedCount++ }
    }
    Log "Cleaned $cleanedCount deployment folders" "OK"

    # dotnet clean
    $cleanResult = RunCommand -Description "dotnet clean" `
        -Command "dotnet" `
        -Arguments @("clean", $slnFile.FullName, "-c", $Configuration, "-v", "quiet") `
        -AllowFailure

    StepComplete "Clean finished"
}
else {
    Log "Skipping clean" "SKIP"
    StepComplete
}

# ══════════════════════════════════════════════════════
# STEP 3: Restore NuGet packages
# ══════════════════════════════════════════════════════
StepHeader "Restoring NuGet packages"

$restoreResult = RunCommand -Description "dotnet restore" `
    -Command "dotnet" `
    -Arguments @("restore", $slnFile.FullName, "-v", "quiet") `
    -AllowFailure

if (-not $restoreResult.Success) {
    # Retry with --force to clear caches
    Log "Restore failed. Retrying with --force..." "WARN"
    RunCommand -Description "dotnet restore --force" `
        -Command "dotnet" `
        -Arguments @("restore", $slnFile.FullName, "--force", "-v", "quiet")
}

StepComplete "Packages restored"

# ══════════════════════════════════════════════════════
# STEP 4: Build solution
# ══════════════════════════════════════════════════════
StepHeader "Building solution"

$buildArgs = @(
    "build", $slnFile.FullName,
    "-c", $Configuration,
    "--no-restore",
    "-v", "minimal"
)

$buildResult = & dotnet @buildArgs 2>&1
$buildExit = $LASTEXITCODE

if ($buildExit -ne 0) {
    # Show build errors
    Log "BUILD ERRORS:" "FAIL"
    $buildResult | Where-Object { $_ -match " error " } | Select-Object -Last 20 | ForEach-Object {
        Write-Host "    $_" -ForegroundColor Red
    }
    FailExit "Build failed"
}

$warningCount = ($buildResult | Where-Object { $_ -match " warning " }).Count
if ($warningCount -gt 0) {
    Log "Build succeeded with $warningCount warnings" "WARN"
}
else {
    StepComplete "Build succeeded with 0 warnings"
}

# ══════════════════════════════════════════════════════
# STEP 5: Run tests
# ══════════════════════════════════════════════════════
StepHeader "Running tests"

if (-not $SkipTests) {
    $totalPassed = 0
    $totalFailed = 0
    $totalSkipped = 0

    foreach ($testProj in $testProjects) {
        $testPath = Join-Path $SolutionRoot $testProj
        if (-not (Test-Path $testPath)) {
            Log "Test project not found: $testProj" "SKIP"
            continue
        }

        $testName = [System.IO.Path]::GetFileNameWithoutExtension($testProj)
        Log "Running: $testName"

        $testArgs = @(
            "test", $testPath,
            "-c", $Configuration,
            "--no-build",
            "--verbosity", "minimal",
            "--logger", "console;verbosity=minimal"
        )

        $testOutput = & dotnet @testArgs 2>&1
        $testExit = $LASTEXITCODE

        # Parse results from output
        foreach ($line in $testOutput) {
            if ($line -match "Passed:\s*(\d+)") { $totalPassed += [int]$Matches[1] }
            if ($line -match "Failed:\s*(\d+)") { $totalFailed += [int]$Matches[1] }
            if ($line -match "Skipped:\s*(\d+)") { $totalSkipped += [int]$Matches[1] }
        }

        if ($testExit -ne 0) {
            $testOutput | Where-Object { $_ -match "Failed|Error" } | Select-Object -Last 10 | ForEach-Object {
                Write-Host "    $_" -ForegroundColor Red
            }
            FailExit "Tests failed in $testName"
        }

        Log "  $testName passed" "OK"
    }

    $totalAll = $totalPassed + $totalFailed + $totalSkipped
    StepComplete "Tests: $totalPassed passed, $totalFailed failed, $totalSkipped skipped of $totalAll total"
}
else {
    Log "Skipping tests" "SKIP"
    StepComplete
}

# ══════════════════════════════════════════════════════
# STEP 6: Publish .NET projects
# ══════════════════════════════════════════════════════
StepHeader "Publishing .NET projects"

$publishSummary = @()

foreach ($proj in $dotnetProjects) {
    $csprojPath = Join-Path $SolutionRoot $proj.Csproj
    $deployPath = Join-Path $DeployRoot $proj.Deploy

    Log "Publishing: $($proj.Name)"

    # Ensure deploy folder exists and is clean
    if (Test-Path $deployPath) {
        SafeRemoveDir $deployPath | Out-Null
    }
    New-Item -Path $deployPath -ItemType Directory -Force | Out-Null

    $pubArgs = @(
        "publish", $csprojPath,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $SelfContained.ToString().ToLower(),
        "-o", $deployPath
    )

    if ($SingleFile) {
        $pubArgs += "-p:PublishSingleFile=true"
        $pubArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    }

    $pubOutput = & dotnet @pubArgs 2>&1
    $pubExit = $LASTEXITCODE

    if ($pubExit -ne 0) {
        Log "Publish failed for $($proj.Name). Output:" "FAIL"
        $pubOutput | Select-Object -Last 15 | ForEach-Object {
            Write-Host "    $_" -ForegroundColor Red
        }
        FailExit "Failed to publish $($proj.Name)"
    }

    # Verify output exists
    $stats = GetFolderStats $deployPath
    if ($stats.Files -eq 0) {
        FailExit "Publish produced no files for $($proj.Name)"
    }

    # Find the main executable
    $mainExe = Get-ChildItem $deployPath -Filter "$($proj.Name).exe" -Depth 0 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $mainExe) {
        $mainExe = Get-ChildItem $deployPath -Filter "$($proj.Name).dll" -Depth 0 -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    $exeName = if ($mainExe) { $mainExe.Name } else { "unknown" }

    $publishSummary += [PSCustomObject]@{
        Project    = $proj.Name
        Files      = $stats.Files
        SizeMB     = $stats.SizeMB
        Executable = $exeName
        Path       = $deployPath
    }

    Log "  $($proj.Name): $($stats.Files) files, $($stats.SizeMB) MB -> $exeName" "OK"
}

StepComplete "$($dotnetProjects.Count) .NET projects published"

# ══════════════════════════════════════════════════════
# STEP 7: Build and publish WebClient
# ══════════════════════════════════════════════════════
StepHeader "Building WebClient"

if (-not $SkipWebClient) {
    $wcSourcePath = Join-Path $SolutionRoot $webClientFolder
    $wcDeployPath = Join-Path $DeployRoot $webClientFolder

    Push-Location $wcSourcePath

    try {
        # Detect Vite or CRA
        $pkgContent = Get-Content "package.json" -Raw | ConvertFrom-Json
        $devDeps = @()
        if ($pkgContent.devDependencies) {
            $devDeps = $pkgContent.devDependencies.PSObject.Properties.Name
        }
        $isVite = $devDeps -contains "vite"
        $buildOutputDir = if ($isVite) { "dist" } else { "build" }
        Log "Detected: $(if ($isVite) { 'Vite' } else { 'Create React App' })"

        # Write .env.production
        $envFileName = if ($isVite) { ".env.production" } else { ".env.production.local" }
        $envVarName = if ($isVite) { "VITE_API_BASE_URL" } else { "REACT_APP_API_BASE_URL" }
        $envContent = "# Generated by Publish-All.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n$envVarName=$WebApiUrl"
        Set-Content -Path $envFileName -Value $envContent -Encoding UTF8
        Log "Wrote $envFileName with $envVarName=$WebApiUrl" "OK"

        # Install dependencies
        Log "Installing npm dependencies..."
        $npmInstallOutput = & npm ci 2>&1
        $npmInstallExit = $LASTEXITCODE

        if ($npmInstallExit -ne 0) {
            Log "npm ci failed, trying npm install..." "WARN"
            $npmInstallOutput = & npm install 2>&1
            $npmInstallExit = $LASTEXITCODE

            if ($npmInstallExit -ne 0) {
                Log "npm install also failed:" "FAIL"
                $npmInstallOutput | Select-Object -Last 10 | ForEach-Object {
                    Write-Host "    $_" -ForegroundColor Red
                }
                FailExit "Failed to install WebClient dependencies"
            }
        }
        Log "Dependencies installed" "OK"

        # Build
        Log "Running npm run build..."
        $npmBuildOutput = & npm run build 2>&1
        $npmBuildExit = $LASTEXITCODE

        if ($npmBuildExit -ne 0) {
            Log "npm run build failed:" "FAIL"
            $npmBuildOutput | Select-Object -Last 20 | ForEach-Object {
                Write-Host "    $_" -ForegroundColor Red
            }
            FailExit "WebClient build failed"
        }
        Log "React build completed" "OK"

        # Verify build output
        $distPath = Join-Path $wcSourcePath $buildOutputDir
        $indexHtml = Join-Path $distPath "index.html"
        if (-not (Test-Path $indexHtml)) {
            FailExit "Build succeeded but index.html not found in $distPath"
        }

        # Copy to deployment folder
        if (Test-Path $wcDeployPath) {
            SafeRemoveDir $wcDeployPath | Out-Null
        }
        New-Item -Path $wcDeployPath -ItemType Directory -Force | Out-Null
        & robocopy $distPath $wcDeployPath /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null

        # Generate IIS SPA web.config
        $webConfigXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="SPA" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
            <add input="{REQUEST_URI}" pattern="^/api" negate="true" />
            <add input="{REQUEST_URI}" pattern="^/hubs" negate="true" />
          </conditions>
          <action type="Rewrite" url="/" />
        </rule>
      </rules>
    </rewrite>
    <staticContent>
      <remove fileExtension=".json" />
      <mimeMap fileExtension=".json" mimeType="application/json" />
      <remove fileExtension=".woff2" />
      <mimeMap fileExtension=".woff2" mimeType="font/woff2" />
      <remove fileExtension=".svg" />
      <mimeMap fileExtension=".svg" mimeType="image/svg+xml" />
    </staticContent>
    <defaultDocument>
      <files><clear /><add value="index.html" /></files>
    </defaultDocument>
  </system.webServer>
</configuration>
"@
        Set-Content -Path (Join-Path $wcDeployPath "web.config") -Value $webConfigXml -Encoding UTF8
        Log "Generated web.config for IIS SPA routing" "OK"

        $wcStats = GetFolderStats $wcDeployPath
        $publishSummary += [PSCustomObject]@{
            Project    = "WebClient"
            Files      = $wcStats.Files
            SizeMB     = $wcStats.SizeMB
            Executable = "index.html"
            Path       = $wcDeployPath
        }

        Log "WebClient: $($wcStats.Files) files, $($wcStats.SizeMB) MB" "OK"
    }
    finally {
        Pop-Location
    }

    StepComplete "WebClient built and deployed"
}
else {
    Log "Skipping WebClient build" "SKIP"
    StepComplete
}

# ══════════════════════════════════════════════════════
# STEP 8: Generate manifest and summary
# ══════════════════════════════════════════════════════
StepHeader "Generating deployment manifest"

# Gather git information for traceability
$gitSha = ""
$gitBranch = ""
try {
    $gitSha = (& git rev-parse HEAD 2>$null)
    $gitBranch = (& git rev-parse --abbrev-ref HEAD 2>$null)
} catch {}
if (-not $gitSha) { $gitSha = "unknown"; Log "Git SHA unavailable" "WARN" }
if (-not $gitBranch) { $gitBranch = "unknown" }

# Compute proto file hash for version compatibility tracking
$protoHash = ""
$protoFile = Join-Path $SolutionRoot "TestAgentGrpc\Protos\test_agent.proto"
if (Test-Path $protoFile) {
    $protoHash = (Get-FileHash $protoFile -Algorithm SHA256).Hash.Substring(0, 16)
}

# Write manifest JSON for downstream tools
$manifest = @{
    generatedAt       = (Get-Date).ToString("o")
    solution          = $slnFile.Name
    configuration     = $Configuration
    runtime           = $Runtime
    selfContained     = $SelfContained
    singleFile        = $SingleFile
    webApiUrl         = $WebApiUrl
    gitSha            = $gitSha
    gitBranch         = $gitBranch
    protoHash         = $protoHash
    agentCapVersion   = "2.0"
    buildMachine      = $env:COMPUTERNAME
    projects          = @()
    checksums         = @{}
}

foreach ($item in $publishSummary) {
    $manifest.projects += @{
        name       = $item.Project
        path       = $item.Path
        files      = $item.Files
        sizeMB     = $item.SizeMB
        executable = $item.Executable
    }

    # Compute checksum of the primary executable for integrity validation
    $exePath = Join-Path $item.Path $item.Executable
    if (Test-Path $exePath) {
        $hash = (Get-FileHash $exePath -Algorithm SHA256).Hash
        $manifest.checksums[$item.Project] = $hash
    }
}

$manifestPath = Join-Path $DeployRoot "deploy-manifest.json"
$manifest | ConvertTo-Json -Depth 3 | Set-Content $manifestPath -Encoding UTF8
Log "Manifest written: $manifestPath" "OK"

StepComplete "Manifest generated"

# ══════════════════════════════════════════════════════
# FINAL SUMMARY
# ══════════════════════════════════════════════════════
$totalElapsed = (Get-Date) - $startTime
$totalTimeStr = $totalElapsed.ToString('mm\:ss')

$totalFiles = 0
$totalSizeMB = 0
foreach ($item in $publishSummary) {
    $totalFiles += $item.Files
    $totalSizeMB += $item.SizeMB
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host "    BUILD AND PUBLISH COMPLETE" -ForegroundColor Green
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host ""

# Summary table
Write-Host "  Project                        Files    Size MB   Executable" -ForegroundColor DarkGray
Write-Host "  -------                        -----    -------   ----------" -ForegroundColor DarkGray

foreach ($item in $publishSummary) {
    $projPad = $item.Project.PadRight(30)
    $filesPad = $item.Files.ToString().PadLeft(5)
    $sizePad = $item.SizeMB.ToString("F1").PadLeft(9)
    Write-Host "  $projPad $filesPad $sizePad   $($item.Executable)" -ForegroundColor White
}

Write-Host "  -------                        -----    -------" -ForegroundColor DarkGray
$totalPad = "TOTAL".PadRight(30)
$tfPad = $totalFiles.ToString().PadLeft(5)
$tsPad = ([math]::Round($totalSizeMB, 1)).ToString("F1").PadLeft(9)
Write-Host "  $totalPad $tfPad $tsPad" -ForegroundColor Cyan
Write-Host ""

Write-Host "  Time:          $totalTimeStr" -ForegroundColor White
Write-Host "  Configuration: $Configuration | $Runtime" -ForegroundColor DarkGray
Write-Host "  Self-contained: $SelfContained | Single-file: $SingleFile" -ForegroundColor DarkGray
Write-Host "  Deploy root:   $DeployRoot" -ForegroundColor DarkGray
Write-Host "  Manifest:      $manifestPath" -ForegroundColor DarkGray

# Warnings
if ($script:warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "  WARNINGS: $($script:warnings.Count)" -ForegroundColor Yellow
    foreach ($w in $script:warnings) {
        Write-Host "    - $w" -ForegroundColor Yellow
    }
}

# Next steps
Write-Host ""
Write-Host "  NEXT STEPS:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Deploy Controller to jvgr22:" -ForegroundColor White
Write-Host "    xcopy /E /Y /I $DeployRoot\TestControllerGrpc \\jvgr22\c$\TestControllerService\" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Deploy WebApi + WebClient to IIS:" -ForegroundColor White
Write-Host "    .\Deploy-TestController.ps1 ``" -ForegroundColor DarkGray
Write-Host "        -WebApiSource $DeployRoot\TestController.WebApi ``" -ForegroundColor DarkGray
Write-Host "        -WebClientSource $DeployRoot\TestController.WebClient" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Deploy Agents to all VMs:" -ForegroundColor White
Write-Host "    .\Deploy-AllAgents.ps1 -AgentSource $DeployRoot\TestAgentGrpc" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host ""

exit 0
