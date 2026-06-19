<#
.SYNOPSIS
    Build and deploy TestController WebAPI + WebClient to IIS (single site).

.DESCRIPTION
    One script to move the WEB TIER to production. It does the following:
      0. Preflight: verify IIS + hosting bundle, ensure the WebSocket feature,
         and check that the WPF Controller (the proxy target) is reachable.
      1. Build React WebClient (npm ci + npm run build)
      2. Copy React output into WebAPI's wwwroot/
      3. Publish WebAPI as framework-dependent for IIS in-process hosting
      4. Write appsettings.Production.json with the WPF Controller upstream URL
      5. Deploy to the target server (local or remote via admin share)
      6. Configure IIS site/pool (warm settings for SignalR + proxy) and start it
      then validates the deployment is serving.

    IMPORTANT - db / proxy context:
      The WebAPI serves the React SPA from wwwroot/, so the Vite DEV proxy is
      not needed in production (the SPA calls same-origin /api and /hubs).
      This is NOT the same as the WebAPI's RUNTIME proxy: the WebAPI owns NO
      database. It registers ThrowingDbContextFactory + Null* stubs and forwards
      every data / auth / orchestration call to the WPF Controller (default
      http://localhost:5200). That Controller process MUST be running and
      reachable for the site to actually work - this script deploys only the
      web half of a two-part system.

.PARAMETER TargetServer
    Server hostname. Default: current machine (localhost).

.PARAMETER SiteName
    IIS site name. Default: TestControllerWeb

.PARAMETER SitePort
    IIS binding port. Default: 81

.PARAMETER DeployPath
    Physical path on the target server. Default: C:\inetpub\TestControllerWeb

.PARAMETER ControllerUrl
    URL of the WPF Controller host that the WebAPI proxies to (the sole DB owner).
    Default: http://localhost:5200  (use a real host:5200 if the Controller runs
    on a different machine than the WebAPI).

.PARAMETER ControllerUrlConfigKey
    The appsettings key (colon-delimited path) the WebAPI reads for the Controller
    upstream address. Default: "Controller:BaseUrl".
    CONFIRM this matches the key your TestController.WebApi actually reads (search
    Program.cs / the proxy or gRPC channel setup) and override if different.

.PARAMETER HealthPath
    Anonymous health endpoint used for validation. Default: "/api/health".
    CONFIRM this route exists and requires no auth (HealthController / system-mode).

.PARAMETER RequireBackend
    If set, the script FAILS when the WPF Controller is not reachable. Default off
    (it warns), so you may deploy the web tier while the Controller is briefly down.

.PARAMETER SkipBuild
    Skip npm + dotnet build. Use when you only need to re-deploy existing output.
    (Step 4 - production config - still runs, so you can re-point ControllerUrl
    without rebuilding.)

.PARAMETER SkipIIS
    Skip IIS configuration. Use for remote targets where you configure IIS separately.

.EXAMPLE
    # Full build + deploy to local machine (Controller on same box, port 5200)
    .\PublishWeb.ps1

.EXAMPLE
    # Deploy to remote server JVGR22; its WebAPI proxies to its local Controller
    .\PublishWeb.ps1 -TargetServer JVGR22 -SitePort 81 -ControllerUrl http://localhost:5200

.EXAMPLE
    # Re-point the Controller upstream and redeploy without rebuilding
    .\PublishWeb.ps1 -SkipBuild -ControllerUrl http://CTRLBOX:5200

.EXAMPLE
    # Fail the deploy if the Controller (DB owner) is not up
    .\PublishWeb.ps1 -RequireBackend
#>

[CmdletBinding()]
param(
    [string]$TargetServer = "localhost",
    [string]$SiteName = "TestControllerWeb",
    [int]$SitePort = 81,
    [string]$DeployPath = "C:\inetpub\TestControllerWeb",
    [string]$Configuration = "Release",
    # ── NEW (db / proxy context) ─────────────────────────────────────
    [string]$ControllerUrl = "http://localhost:5200",
    [string]$ControllerUrlConfigKey = "Controller:BaseUrl",
    [string]$HealthPath = "/api/health",
    [switch]$RequireBackend,
    # ─────────────────────────────────────────────────────────────────
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

# Parse the Controller upstream into host/port for the reachability probe
try {
    $ctrlUri  = [Uri]$ControllerUrl
    $ctrlHost = $ctrlUri.Host
    $ctrlPort = $ctrlUri.Port
}
catch {
    Write-Host "ERROR: -ControllerUrl '$ControllerUrl' is not a valid URL." -ForegroundColor Red
    exit 1
}

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

# NEW: TCP reachability test (works for gRPC/HTTP2 upstream where a plain GET would not return 200)
function Test-TcpPort([string]$hostName, [int]$port, [int]$timeoutMs = 3000) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $iar = $client.BeginConnect($hostName, $port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne($timeoutMs)
        if ($ok -and $client.Connected) { $client.EndConnect($iar); $client.Close(); return $true }
        $client.Close(); return $false
    }
    catch { return $false }
}

# NEW: PSCustomObject -> hashtable (works on Windows PowerShell 5.1 and PowerShell 7)
function ConvertPSObjectToHashtable($obj) {
    if ($null -eq $obj) { return @{} }
    if ($obj -is [hashtable]) { return $obj }
    $ht = @{}
    foreach ($p in $obj.PSObject.Properties) {
        $v = $p.Value
        if ($v -is [System.Management.Automation.PSCustomObject]) { $ht[$p.Name] = ConvertPSObjectToHashtable $v }
        else { $ht[$p.Name] = $v }
    }
    return $ht
}

# NEW: write/merge a colon-delimited key into appsettings.Production.json (non-destructive)
function Write-ProductionConfig([string]$publishDir, [string]$keyPath, [string]$value) {
    $cfgPath = Join-Path $publishDir "appsettings.Production.json"
    $root = @{}
    if (Test-Path $cfgPath) {
        try { $root = ConvertPSObjectToHashtable (Get-Content $cfgPath -Raw | ConvertFrom-Json) }
        catch { $root = @{} }
    }
    if (-not $root) { $root = @{} }

    $parts = $keyPath.Split(":")
    $node = $root
    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
        $k = $parts[$i]
        if (-not $node.ContainsKey($k) -or -not ($node[$k] -is [hashtable])) { $node[$k] = @{} }
        $node = $node[$k]
    }
    $node[$parts[-1]] = $value

    ($root | ConvertTo-Json -Depth 20) | Set-Content -Path $cfgPath -Encoding UTF8
    return $cfgPath
}

# NEW: ensure the IIS WebSocket feature is present (web.config <webSocket> alone is not enough)
function Ensure-WebSocketFeature {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $isServer = ($os.ProductType -ne 1)   # 1 = workstation
        if ($isServer -and (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) {
            $f = Get-WindowsFeature Web-WebSockets -ErrorAction SilentlyContinue
            if ($f -and -not $f.Installed) {
                Install-WindowsFeature Web-WebSockets -ErrorAction Stop | Out-Null
                Log "Installed IIS WebSockets feature (Web-WebSockets)" "OK"
            }
            else { Log "IIS WebSockets feature present" "OK" }
        }
        elseif (Get-Command Enable-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
            $f = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -ErrorAction SilentlyContinue
            if ($f -and $f.State -ne "Enabled") {
                Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart -ErrorAction Stop | Out-Null
                Log "Enabled IIS WebSockets feature (IIS-WebSockets)" "OK"
            }
            else { Log "IIS WebSockets feature present" "OK" }
        }
        else {
            Log "Could not determine how to verify the WebSocket feature on this OS." "WARN"
        }
    }
    catch {
        Log "Could not verify/install the IIS WebSocket feature - SignalR may fall back to long-polling. $($_.Exception.Message)" "WARN"
    }
}

# ─── Banner ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   PublishWeb: WebAPI + WebClient → IIS" -ForegroundColor Cyan
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Log "Solution:      $SolutionRoot"
Log "Target:        ${TargetServer}:${SitePort} ($SiteName)"
Log "Deploy to:     $DeployPath"
Log "Controller:    $ControllerUrl   (key: $ControllerUrlConfigKey)"
Log "Skip build:    $SkipBuild"
Write-Host ""

# ═════════════════════════════════════════════════════════════════════
# STEP 0: Preflight (fail fast before a long build)
# ═════════════════════════════════════════════════════════════════════
Log "═══ STEP 0: Preflight checks ═══" "STEP"

# Elevation: local IIS configuration uses appcmd / icacls / firewall / Windows
# features, all of which require Administrator. Fail fast here (before the long
# build) rather than half-deploying and dying at the first IIS call. A remote
# target or -SkipIIS run doesn't touch local IIS, so elevation isn't required.
if ($isLocal -and -not $SkipIIS) {
    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Fail "Local IIS deploy requires an elevated (Run as Administrator) PowerShell session. Re-run elevated, or use -SkipIIS to deploy files only."
    }
    Log "Running elevated" "OK"
}

# IIS + hosting bundle + WebSocket feature (local IIS deploys only)
if ($isLocal -and -not $SkipIIS) {
    $appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
    if (-not (Test-Path $appcmd)) {
        Fail "IIS not installed. Install IIS + the ASP.NET Core Hosting Bundle first."
    }
    # Soft check for the ASP.NET Core Module (Hosting Bundle)
    $modules = & $appcmd list module 2>$null
    if ($modules -notmatch "AspNetCoreModuleV2") {
        Log "AspNetCoreModuleV2 not detected - install the ASP.NET Core Hosting Bundle, or IIS cannot host the app." "WARN"
    }
    else { Log "ASP.NET Core Module present" "OK" }

    Ensure-WebSocketFeature
}
elseif ($SkipIIS) {
    Log "SkipIIS set - not checking IIS features." "INFO"
}

# Controller (proxy target / DB owner) reachability
if ($isLocal) {
    if (Test-TcpPort $ctrlHost $ctrlPort 3000) {
        Log "WPF Controller reachable at ${ctrlHost}:${ctrlPort} (proxy target up)" "OK"
    }
    else {
        $cmsg = "WPF Controller NOT reachable at ${ctrlHost}:${ctrlPort}. The WebAPI proxies all data/auth/trigger operations there - without it the SPA will load but every /api call fails. Ensure the WPF Controller is running."
        if ($RequireBackend) { Fail $cmsg } else { Log $cmsg "WARN" }
    }
}
else {
    Log "Remote deploy: cannot probe the Controller from here. Ensure the WebAPI on $TargetServer can reach $ControllerUrl (the Controller must be running on that side)." "WARN"
}
Write-Host ""

# ═════════════════════════════════════════════════════════════════════
# STEP 1: Build React WebClient
# ═════════════════════════════════════════════════════════════════════
if (-not $SkipBuild) {
    Log "═══ STEP 1/6: Building React WebClient ═══" "STEP"

    if (-not (Test-Path (Join-Path $WebClientDir "package.json"))) {
        Fail "package.json not found in $WebClientDir"
    }

    Push-Location $WebClientDir

    # Clear VITE_API_BASE_URL for production (same-origin, no dev proxy needed)
    $envProd = Join-Path $WebClientDir ".env.production"
    Set-Content -Path $envProd -Value "# Production: API served from same origin`nVITE_API_BASE_URL=" -Encoding UTF8
    Log "Wrote .env.production (same-origin API)" "OK"

    # Install deps — stop any running dev server that may lock node_modules.
    # Use CIM for the command line: Process.CommandLine is not available on
    # Windows PowerShell 5.1 (added in PS 7), so the old Get-Process filter
    # silently matched nothing and never killed the locking dev server.
    $viteProc = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "vite" } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
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
    Log "═══ STEP 2/6: Copying React → WebAPI wwwroot ═══" "STEP"

    $wwwroot = Join-Path $WebApiDir "wwwroot"
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
    New-Item -Path $wwwroot -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $distDir "*") -Destination $wwwroot -Recurse -Force
    Log "Copied dist/ → wwwroot/ ($((Get-ChildItem $wwwroot -Recurse -File).Count) files)" "OK"

    # ═════════════════════════════════════════════════════════════════════
    # STEP 3: Publish .NET WebAPI
    # ═════════════════════════════════════════════════════════════════════
    Log "═══ STEP 3/6: Publishing WebAPI (.NET) ═══" "STEP"

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
# STEP 4: Write production config (Controller upstream)   ← NEW
#   The WebAPI owns no DB; it must know where the WPF Controller lives.
#   Written to appsettings.Production.json so the base appsettings.json is
#   left untouched (ASPNETCORE_ENVIRONMENT=Production overlays it).
# ═════════════════════════════════════════════════════════════════════
Log "═══ STEP 4/6: Writing production config (Controller upstream) ═══" "STEP"
$cfgWritten = Write-ProductionConfig $PublishDir $ControllerUrlConfigKey $ControllerUrl
Log "Set '$ControllerUrlConfigKey' = '$ControllerUrl' in $(Split-Path $cfgWritten -Leaf)" "OK"
Log "CONFIRM '$ControllerUrlConfigKey' is the key TestController.WebApi actually reads for the Controller address." "WARN"

# ═════════════════════════════════════════════════════════════════════
# STEP 5: Deploy to target server
# ═════════════════════════════════════════════════════════════════════
Log "═══ STEP 5/6: Deploying to $TargetServer ═══" "STEP"

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

# Ensure web.config exists for IIS hosting (InProcess is safe here: the WebAPI
# never opens the SQLite DB, so there is no app-pool-identity file lock).
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
# STEP 6: Configure and start IIS
# ═════════════════════════════════════════════════════════════════════
if (-not $SkipIIS) {
    Log "═══ STEP 6/6: Configuring IIS ═══" "STEP"

    if (-not $isLocal) {
        Log "Remote target - configure IIS on $TargetServer manually or run this script there." "WARN"
        Log "  Site physical path: $DeployPath" "INFO"
        Log "  App pool: No Managed Code, InProcess, idleTimeout 0, AlwaysRunning" "INFO"
        Log "  Binding: http *:${SitePort}:" "INFO"
        Log "  Ensure the WebSocket feature is installed and the Controller at $ControllerUrl is reachable." "INFO"
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

        # NEW: warm settings - keep the proxy channel + SignalR alive
        #   idle shutdown / scheduled recycle would drop live updates and cold-start /api.
        & $appcmd set apppool /apppool.name:"$SiteName" /processModel.idleTimeout:"00:00:00" 2>$null | Out-Null
        & $appcmd set apppool /apppool.name:"$SiteName" /startMode:"AlwaysRunning" 2>$null | Out-Null
        & $appcmd set apppool /apppool.name:"$SiteName" /recycling.periodicRestart.time:"00:00:00" 2>$null | Out-Null
        Log "App pool warmed: idleTimeout 0, AlwaysRunning, no scheduled recycle" "OK"

        # Create site
        & $appcmd add site /name:"$SiteName" /physicalPath:"$DeployPath" /bindings:"http/*:${SitePort}:" 2>$null | Out-Null
        & $appcmd set site /site.name:"$SiteName" /[path='/'].applicationPool:"$SiteName" 2>$null | Out-Null
        & $appcmd set site /site.name:"$SiteName" /serverAutoStart:"true" 2>$null | Out-Null
        Log "Created site: $SiteName → port $SitePort" "OK"

        # NEW: try to enable preload (requires the Application Initialization feature) - non-fatal
        & $appcmd set app /app.name:"$SiteName/" /preloadEnabled:"true" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Log "Preload enabled (Application Initialization)" "OK" }
        else { Log "Could not enable preload - install the 'Application Initialization' IIS feature for warm starts (optional)." "WARN" }

        # Permissions
        icacls $DeployPath /grant "IIS AppPool\${SiteName}:(OI)(CI)RX" /T /Q 2>$null | Out-Null
        icacls (Join-Path $DeployPath "logs") /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T /Q 2>$null | Out-Null
        Log "Permissions set" "OK"

        # Firewall rule (non-fatal: a disabled firewall service or missing
        # NetSecurity cmdlets must not abort an otherwise-deployed site).
        try {
            Remove-NetFirewallRule -DisplayName "TestControllerWeb" -ErrorAction SilentlyContinue
            New-NetFirewallRule -DisplayName "TestControllerWeb" -Direction Inbound -Protocol TCP -LocalPort $SitePort -Action Allow -Profile Any -ErrorAction Stop | Out-Null
            Log "Firewall rule: port $SitePort open" "OK"
        }
        catch {
            Log "Could not set firewall rule for port $SitePort - open it manually if the site is unreachable. $($_.Exception.Message)" "WARN"
        }

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

# 1) Static SPA must serve (proves IIS + static files; cold InProcess start can be slow)
$staticOk = $false
for ($i = 1; $i -le 6; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri $testUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $staticOk = $true; break }
    }
    catch {
        if ($i -lt 6) { Start-Sleep -Seconds 4 }
    }
}
if ($staticOk) {
    Log "Static SPA serving: HTTP 200 at $testUrl" "OK"
}
else {
    Log "Static SPA NOT serving at $testUrl (may still be warming up)" "WARN"
    Log "  Check: Event Viewer → Windows Logs → Application for ASP.NET Core errors" "WARN"
    Log "  Check: $DeployPath\logs\stdout* for stdout logs" "WARN"
}

# 2) WebAPI health (anonymous) - confirms the .NET process is alive
$healthUrl = "$testUrl$HealthPath"
$healthOk = $false
try {
    $h = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    if ($h.StatusCode -eq 200) { $healthOk = $true }
}
catch { }
if ($healthOk) {
    Log "WebAPI alive: GET $HealthPath → 200" "OK"
}
else {
    Log "WebAPI health probe failed at $healthUrl (confirm $HealthPath exists and is anonymous)" "WARN"
}

# 3) Backend / proxy target (the db context) - the WebAPI proxies data ops to the WPF Controller
if ($isLocal) {
    if (Test-TcpPort $ctrlHost $ctrlPort 3000) {
        Log "WPF Controller reachable at ${ctrlHost}:${ctrlPort} - data/auth/trigger path can work" "OK"
    }
    else {
        $bmsg = "WPF Controller NOT reachable at ${ctrlHost}:${ctrlPort}. The SPA will load, but every /api call that needs data/auth will fail until the Controller is running."
        if ($RequireBackend) { Fail $bmsg } else { Log $bmsg "WARN" }
    }
}
else {
    Log "Remote: confirm the WebAPI on $TargetServer can reach the Controller at $ControllerUrl." "WARN"
}

# ═════════════════════════════════════════════════════════════════════
# Summary
# ═════════════════════════════════════════════════════════════════════
$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "   WEB TIER DEPLOYMENT COMPLETE ($($elapsed.ToString('mm\:ss')))" -ForegroundColor Green
Write-Host "  ══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  URL:        $testUrl" -ForegroundColor White
Write-Host "  API:        $testUrl/api/" -ForegroundColor White
Write-Host "  SignalR:    $testUrl/hubs/controller" -ForegroundColor White
Write-Host "  Health:     $testUrl$HealthPath" -ForegroundColor White
Write-Host "  Files:      $DeployPath" -ForegroundColor DarkGray
Write-Host "  Logs:       $DeployPath\logs\" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Controller: $ControllerUrl   (config key: $ControllerUrlConfigKey)" -ForegroundColor White
Write-Host ""
Write-Host "  The React app and WebAPI are served from the SAME IIS site (no dev proxy)." -ForegroundColor DarkGray
Write-Host "  The WebAPI owns NO database - it proxies data/auth/orchestration to the" -ForegroundColor DarkGray
Write-Host "  WPF Controller above. That process MUST be running for the site to work." -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Reminder: confirm '$ControllerUrlConfigKey' matches the key your" -ForegroundColor DarkGray
Write-Host "  TestController.WebApi reads for the Controller address." -ForegroundColor DarkGray
Write-Host ""