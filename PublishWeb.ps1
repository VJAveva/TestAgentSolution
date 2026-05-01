<#
.SYNOPSIS
    Build and deploy TestController WebAPI + WebClient to IIS (single site).

.DESCRIPTION
    One script to move everything to production. Does the following:
      1. Build React WebClient (npm ci + npm run build)
      2. Copy React output into WebAPI's wwwroot/
      3. Publish WebAPI as framework-dependent for IIS in-process hosting
      4. Deploy to the target server (local or remote via admin share)
      5. Configure IIS site/pool if deploying locally
      6. Validate the deployment is serving

    The WebAPI serves the React SPA from wwwroot/ and proxies are NOT needed
    in production - /api and /hubs routes are handled by ASP.NET directly.

.PARAMETER TargetServer
    Server hostname. Default: current machine (localhost).

.PARAMETER SiteName
    IIS site name. Default: TestControllerWeb

.PARAMETER SitePort
    IIS binding port. Default: 81

.PARAMETER DeployPath
    Physical path on the target server. Default: C:\inetpub\TestControllerWeb

.PARAMETER SkipBuild
    Skip npm + dotnet build. Use when you only need to re-deploy existing output.

.PARAMETER SkipIIS
    Skip IIS configuration. Use for remote targets where you configure IIS separately.

.EXAMPLE
    # Full build + deploy to local machine
    .\PublishWeb.ps1

.EXAMPLE
    # Deploy to remote server JVGR22 on port 81
    .\PublishWeb.ps1 -TargetServer JVGR22 -SitePort 81

.EXAMPLE
    # Re-deploy without rebuilding
    .\PublishWeb.ps1 -SkipBuild
#>

[CmdletBinding()]
param(
    [string]$TargetServer = "localhost",
    [string]$SiteName = "TestControllerWeb",
    [int]$SitePort = 81,
    [string]$DeployPath = "C:\inetpub\TestControllerWeb",
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SkipIIS
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date

# ─── Paths ───────────────────────────────────────────────────────────
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = (Get-Location).Path }

# Support running from solution root or from the scripts folder
$SolutionRoot = $ScriptDir
if (-not (Test-Path (Join-Path $SolutionRoot "TestController.WebApi"))) {
    $SolutionRoot = Split-Path $ScriptDir -Parent
}
if (-not (Test-Path (Join-Path $SolutionRoot "TestController.WebApi"))) {
    Write-Host "ERROR: Cannot find TestController.WebApi project. Run from solution root." -ForegroundColor Red
    exit 1
}

$WebClientDir = Join-Path $SolutionRoot "TestController.WebClient"
$WebApiDir    = Join-Path $SolutionRoot "TestController.WebApi"
$WebApiCsproj = Join-Path $WebApiDir "TestController.WebApi.csproj"
$PublishDir   = Join-Path $SolutionRoot "publish\webapi"

$isLocal = ($TargetServer -eq "localhost") -or
           ($TargetServer -eq $env:COMPUTERNAME) -or
           ($TargetServer -eq ".")

# ─── Helpers ─────────────────────────────────────────────────────────
function Log([string]$msg, [string]$lvl = "INFO") {
    $ts = (Get-Date).ToString("HH:mm:ss")
    $color = switch ($lvl) { "OK" {"Green"} "WARN" {"Yellow"} "FAIL" {"Red"} "STEP" {"Cyan"} default {"White"} }
    Write-Host "[$ts] $msg" -ForegroundColor $color
}

function Fail([string]$msg) {
    Log "FAILED: $msg" "FAIL"
    exit 1
}

# ─── Banner ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   PublishWeb: WebAPI + WebClient → IIS" -ForegroundColor Cyan
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Log "Solution:    $SolutionRoot"
Log "Target:      ${TargetServer}:${SitePort} ($SiteName)"
Log "Deploy to:   $DeployPath"
Log "Skip build:  $SkipBuild"
Write-Host ""

# ═════════════════════════════════════════════════════════════════════
# STEP 1: Build React WebClient
# ═════════════════════════════════════════════════════════════════════
if (-not $SkipBuild) {
    Log "═══ STEP 1/5: Building React WebClient ═══" "STEP"

    if (-not (Test-Path (Join-Path $WebClientDir "package.json"))) {
        Fail "package.json not found in $WebClientDir"
    }

    Push-Location $WebClientDir

    # Clear VITE_API_BASE_URL for production (same-origin, no proxy needed)
    $envProd = Join-Path $WebClientDir ".env.production"
    Set-Content -Path $envProd -Value "# Production: API served from same origin`nVITE_API_BASE_URL=" -Encoding UTF8
    Log "Wrote .env.production (same-origin API)" "OK"

    # Install deps — stop any running dev server that may lock node_modules
    $viteProc = Get-Process -Name "node" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -match "vite" -or $_.MainWindowTitle -match "vite"
    }
    if ($viteProc) {
        Log "Stopping Vite dev server (PID $($viteProc.Id -join ', '))..." "WARN"
        $viteProc | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    Log "Running npm ci..."
    $ErrorActionPreference = "Continue"
    $npmOut = & npm ci 2>&1
    $npmExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($npmExit -ne 0) {
        Log "npm ci failed (files may be locked), trying npm install..." "WARN"
        $ErrorActionPreference = "Continue"
        $npmOut = & npm install 2>&1
        $npmExit = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($npmExit -ne 0) { Pop-Location; Fail "npm install failed: $($npmOut | Select-Object -Last 3)" }
    }
    Log "Dependencies installed" "OK"

    # Build
    Log "Running npm run build..."
    $ErrorActionPreference = "Continue"
    $buildOutput = & npm run build 2>&1
    $buildExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($buildExit -ne 0) {
        $buildOutput | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        Pop-Location; Fail "npm run build failed"
    }

    $distDir = Join-Path $WebClientDir "dist"
    if (-not (Test-Path (Join-Path $distDir "index.html"))) {
        Pop-Location; Fail "Build succeeded but dist/index.html not found"
    }

    $fileCount = (Get-ChildItem $distDir -Recurse -File).Count
    Log "React build complete: $fileCount files" "OK"

    Pop-Location

    # ═════════════════════════════════════════════════════════════════════
    # STEP 2: Copy React output to WebAPI wwwroot
    # ═════════════════════════════════════════════════════════════════════
    Log "═══ STEP 2/5: Copying React → WebAPI wwwroot ═══" "STEP"

    $wwwroot = Join-Path $WebApiDir "wwwroot"
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
    New-Item -Path $wwwroot -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $distDir "*") -Destination $wwwroot -Recurse -Force
    Log "Copied dist/ → wwwroot/ ($((Get-ChildItem $wwwroot -Recurse -File).Count) files)" "OK"

    # ═════════════════════════════════════════════════════════════════════
    # STEP 3: Publish .NET WebAPI
    # ═════════════════════════════════════════════════════════════════════
    Log "═══ STEP 3/5: Publishing WebAPI (.NET) ═══" "STEP"

    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    $pubArgs = @(
        "publish", $WebApiCsproj,
        "-c", $Configuration,
        "-o", $PublishDir,
        "--no-self-contained",
        "-p:SkipSpaPublish=true"
    )

    Log "dotnet publish -c $Configuration..."
    $ErrorActionPreference = "Continue"
    $pubOutput = & dotnet @pubArgs 2>&1
    $pubExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($pubExit -ne 0) {
        $pubOutput | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Fail "dotnet publish failed"
    }

    # Verify key files
    if (-not (Test-Path (Join-Path $PublishDir "TestController.WebApi.dll"))) {
        Fail "Published output missing TestController.WebApi.dll"
    }
    if (-not (Test-Path (Join-Path $PublishDir "wwwroot\index.html"))) {
        Fail "Published output missing wwwroot/index.html (React not included)"
    }

    $pubFiles = (Get-ChildItem $PublishDir -Recurse -File).Count
    $pubSize = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Log "Published: $pubFiles files, ${pubSize} MB" "OK"
}
else {
    Log "═══ Skipping build (using existing $PublishDir) ═══" "STEP"
    if (-not (Test-Path (Join-Path $PublishDir "TestController.WebApi.dll"))) {
        Fail "No published output found. Run without -SkipBuild first."
    }
    Log "Using existing publish output" "OK"
}

# ═════════════════════════════════════════════════════════════════════
# STEP 4: Deploy to target server
# ═════════════════════════════════════════════════════════════════════
Log "═══ STEP 4/5: Deploying to $TargetServer ═══" "STEP"

if ($isLocal) {
    $targetPath = $DeployPath
}
else {
    # Map to admin share: \\SERVER\C$\inetpub\TestControllerWeb
    $driveLetter = $DeployPath.Substring(0, 1)
    $remainder = $DeployPath.Substring(2)
    $targetPath = "\\$TargetServer\$driveLetter`$$remainder"
}

# Stop IIS site before overwriting files
if ($isLocal -and -not $SkipIIS) {
    $appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
    if (Test-Path $appcmd) {
        & $appcmd stop site /site.name:"$SiteName" 2>$null | Out-Null
        & $appcmd stop apppool /apppool.name:"$SiteName" 2>$null | Out-Null
        Start-Sleep -Seconds 2
        Log "Stopped IIS site: $SiteName" "OK"
    }
}

# Clean and copy
if (Test-Path $targetPath) {
    Remove-Item $targetPath -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}
New-Item -Path $targetPath -ItemType Directory -Force | Out-Null

# Use robocopy for reliable copying (handles locked files, retries)
$roboArgs = @($PublishDir, $targetPath, "/E", "/R:3", "/W:2", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")
& robocopy @roboArgs | Out-Null
if ($LASTEXITCODE -gt 7) {
    Fail "robocopy failed (exit code $LASTEXITCODE)"
}

# Ensure web.config exists for IIS hosting
$webConfigDest = Join-Path $targetPath "web.config"
if (-not (Test-Path $webConfigDest)) {
    $webCfg = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
    </handlers>
    <aspNetCore processPath="dotnet" arguments=".\TestController.WebApi.dll"
                stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
                hostingModel="InProcess"
                requestTimeout="04:00:00">
      <environmentVariables>
        <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
      </environmentVariables>
    </aspNetCore>
    <webSocket enabled="true" />
  </system.webServer>
</configuration>
"@
    Set-Content -Path $webConfigDest -Value $webCfg -Encoding UTF8
}

# Ensure logs folder exists for stdout logging
$logsDir = Join-Path $targetPath "logs"
if (-not (Test-Path $logsDir)) { New-Item $logsDir -ItemType Directory -Force | Out-Null }

$deployedFiles = (Get-ChildItem $targetPath -Recurse -File).Count
Log "Deployed $deployedFiles files to $targetPath" "OK"

# Verify critical files
$checks = @("TestController.WebApi.dll", "wwwroot\index.html", "web.config", "appsettings.json")
foreach ($f in $checks) {
    $fp = Join-Path $targetPath $f
    if (-not (Test-Path $fp)) {
        Log "WARNING: Missing $f at deploy target" "WARN"
    }
}

# ═════════════════════════════════════════════════════════════════════
# STEP 5: Configure and start IIS
# ═════════════════════════════════════════════════════════════════════
if (-not $SkipIIS) {
    Log "═══ STEP 5/5: Configuring IIS ═══" "STEP"

    if (-not $isLocal) {
        Log "Remote target - configure IIS on $TargetServer manually or run this script there." "WARN"
        Log "  Site physical path: $DeployPath" "INFO"
        Log "  App pool: No Managed Code, InProcess" "INFO"
        Log "  Binding: http *:${SitePort}:" "INFO"
    }
    else {
        $appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
        if (-not (Test-Path $appcmd)) {
            Fail "IIS not installed. Install IIS + ASP.NET Core Hosting Bundle first."
        }

        # Ensure services running
        Start-Service WAS -ErrorAction SilentlyContinue
        Start-Service W3SVC -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1

        # Remove existing
        $existsSite = & $appcmd list site /name:"$SiteName" 2>$null
        if ($existsSite) {
            & $appcmd delete site /site.name:"$SiteName" 2>$null | Out-Null
        }
        $existsPool = & $appcmd list apppool /name:"$SiteName" 2>$null
        if ($existsPool) {
            & $appcmd stop apppool /apppool.name:"$SiteName" 2>$null | Out-Null
            Start-Sleep -Seconds 1
            & $appcmd delete apppool /apppool.name:"$SiteName" 2>$null | Out-Null
        }

        # Create app pool (No Managed Code for ASP.NET Core)
        & $appcmd add apppool /name:"$SiteName" /managedRuntimeVersion:"" 2>$null | Out-Null
        Log "Created app pool: $SiteName (No Managed Code)" "OK"

        # Create site
        & $appcmd add site /name:"$SiteName" /physicalPath:"$DeployPath" /bindings:"http/*:${SitePort}:" 2>$null | Out-Null
        & $appcmd set site /site.name:"$SiteName" /[path='/'].applicationPool:"$SiteName" 2>$null | Out-Null
        Log "Created site: $SiteName → port $SitePort" "OK"

        # Permissions
        icacls $DeployPath /grant "IIS AppPool\${SiteName}:(OI)(CI)RX" /T /Q 2>$null | Out-Null
        icacls (Join-Path $DeployPath "logs") /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T /Q 2>$null | Out-Null
        Log "Permissions set" "OK"

        # Firewall rule
        Remove-NetFirewallRule -DisplayName "TestControllerWeb" -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName "TestControllerWeb" -Direction Inbound -Protocol TCP -LocalPort $SitePort -Action Allow -Profile Any | Out-Null
        Log "Firewall rule: port $SitePort open" "OK"

        # Start
        & $appcmd start apppool /apppool.name:"$SiteName" 2>$null | Out-Null
        & $appcmd start site /site.name:"$SiteName" 2>$null | Out-Null
        Start-Sleep -Seconds 3
        Log "IIS site started" "OK"
    }
}
else {
    Log "═══ Skipping IIS configuration ═══" "STEP"
}

# ═════════════════════════════════════════════════════════════════════
# Validation
# ═════════════════════════════════════════════════════════════════════
$testUrl = if ($isLocal) { "http://localhost:$SitePort" } else { "http://${TargetServer}:$SitePort" }

Log "Validating deployment at $testUrl ..."

$ok = $false
for ($i = 1; $i -le 5; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri $testUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
    }
    catch {
        if ($i -lt 5) { Start-Sleep -Seconds 3 }
    }
}

if ($ok) {
    Log "Site responding: HTTP 200 at $testUrl" "OK"
}
else {
    Log "Site not responding at $testUrl (may need a moment to warm up)" "WARN"
    Log "  Check: Event Viewer → Windows Logs → Application for ASP.NET Core errors" "WARN"
    Log "  Check: $DeployPath\logs\stdout* for stdout logs" "WARN"
}

# Also test API
try {
    $apiResp = Invoke-WebRequest -Uri "$testUrl/api/results/builds" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    if ($apiResp.StatusCode -eq 200) {
        Log "API responding: GET /api/results/builds → 200" "OK"
    }
}
catch {
    Log "API not responding yet at $testUrl/api/results/builds" "WARN"
}

# ═════════════════════════════════════════════════════════════════════
# Summary
# ═════════════════════════════════════════════════════════════════════
$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "   DEPLOYMENT COMPLETE ($($elapsed.ToString('mm\:ss')))" -ForegroundColor Green
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  URL:     $testUrl" -ForegroundColor White
Write-Host "  API:     $testUrl/api/" -ForegroundColor White
Write-Host "  SignalR: $testUrl/hubs/controller" -ForegroundColor White
Write-Host "  Files:   $DeployPath" -ForegroundColor DarkGray
Write-Host "  Logs:    $DeployPath\logs\" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  The React app and WebAPI are served from the SAME IIS site." -ForegroundColor DarkGray
Write-Host "  No proxy configuration needed in production." -ForegroundColor DarkGray
Write-Host ""
