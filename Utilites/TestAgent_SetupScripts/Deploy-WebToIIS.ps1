<#
.SYNOPSIS
    Deploy an ALREADY-PUBLISHED TestController WebAPI + WebClient folder to local IIS.

.DESCRIPTION
    Run this ON the Controller node (e.g. JVGR22) when the web app is already built
    and sitting in a folder. It does NOT build anything - it just turns that folder
    into a running IIS site:
      1. Validate the published folder (must contain TestController.WebApi.dll + wwwroot)
      2. Write appsettings.Production.json with the WPF Controller upstream URL
      3. Ensure web.config + logs folder
      4. Create/replace the IIS app pool + site + binding (warm settings for SignalR)
      5. Set permissions, open the firewall port, start the site
      6. Validate (static SPA, health, Controller reachability)

    Two fixes vs the build+deploy script:
      - It points IIS at the folder where your files ACTUALLY are (no solution layout,
        no publish\webapi subfolder, no npm/dotnet build).
      - It does NOT run the slow Windows "WebSocket feature" check by default - that is
        what was hanging at "Collecting data... %". SignalR still works (it falls back to
        long-polling). Pass -EnsureWebSockets to install it (can be slow), or install
        "WebSocket Protocol" once via Server Manager.

    The WebAPI owns NO database - it proxies all data/auth/trigger calls to the WPF
    Controller (default http://localhost:5200), which MUST be running for the site to work.

.PARAMETER AppPath
    Folder that already contains the published app (TestController.WebApi.dll, .exe,
    wwwroot\, appsettings.json). IIS is pointed at THIS folder.
    Default: C:\Deployment\TestController.WebApi

.PARAMETER SiteName              Default: TestControllerWeb
.PARAMETER SitePort              Default: 8080
.PARAMETER ControllerUrl         Default: http://localhost:5200
.PARAMETER ControllerUrlConfigKey  Default: Controller:BaseUrl  (confirm the WebAPI reads this key)
.PARAMETER HealthPath            Default: /api/health
.PARAMETER EnsureWebSockets      Switch. Install the IIS WebSocket feature (slow). Off by default.
.PARAMETER RequireBackend        Switch. Fail if the WPF Controller is not reachable. Off by default (warns).

.EXAMPLE
    .\Deploy-WebToIIS.ps1 -SitePort 8080

.EXAMPLE
    .\Deploy-WebToIIS.ps1 -AppPath C:\Deployment\TestController.WebApi -SitePort 8080 -ControllerUrl http://localhost:5200
#>

[CmdletBinding()]
param(
    [string]$AppPath = "C:\Deployment\TestController.WebApi",
    [string]$SiteName = "TestControllerWeb",
    [int]$SitePort = 8080,
    [string]$ControllerUrl = "http://localhost:5200",
    [string]$ControllerUrlConfigKey = "Controller:BaseUrl",
    [string]$HealthPath = "/api/health",
    [switch]$EnsureWebSockets,
    [switch]$RequireBackend
)

$ErrorActionPreference = "Stop"
$startTime = Get-Date

function Log([string]$msg, [string]$lvl = "INFO") {
    $ts = (Get-Date).ToString("HH:mm:ss")
    $color = switch ($lvl) { "OK" {"Green"} "WARN" {"Yellow"} "FAIL" {"Red"} "STEP" {"Cyan"} default {"White"} }
    Write-Host "[$ts] $msg" -ForegroundColor $color
}
function Fail([string]$msg) { Log "FAILED: $msg" "FAIL"; exit 1 }

function Test-TcpPort([string]$hostName, [int]$port, [int]$timeoutMs = 3000) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $iar = $client.BeginConnect($hostName, $port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne($timeoutMs)
        if ($ok -and $client.Connected) { $client.EndConnect($iar); $client.Close(); return $true }
        $client.Close(); return $false
    } catch { return $false }
}

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

function Write-ProductionConfig([string]$dir, [string]$keyPath, [string]$value) {
    $cfgPath = Join-Path $dir "appsettings.Production.json"
    $root = @{}
    if (Test-Path $cfgPath) {
        try { $root = ConvertPSObjectToHashtable (Get-Content $cfgPath -Raw | ConvertFrom-Json) } catch { $root = @{} }
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

# Banner (ASCII only - no garbled box characters in Windows PowerShell 5.1)
Write-Host ""
Write-Host "===========================================================" -ForegroundColor Cyan
Write-Host "  Deploy pre-built Web app to local IIS" -ForegroundColor Cyan
Write-Host "===========================================================" -ForegroundColor Cyan
Log "App folder:  $AppPath"
Log "Site:        $SiteName  (port $SitePort)"
Log "Controller:  $ControllerUrl   (key: $ControllerUrlConfigKey)"
Write-Host ""

# Parse controller host/port for the reachability probe
try { $ctrlUri = [Uri]$ControllerUrl; $ctrlHost = $ctrlUri.Host; $ctrlPort = $ctrlUri.Port }
catch { Fail "-ControllerUrl '$ControllerUrl' is not a valid URL." }

# --- Elevation ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run this in an elevated (Run as Administrator) PowerShell window."
}
Log "Running elevated" "OK"

# --- IIS present ---
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) { Fail "IIS not installed. Install IIS + the ASP.NET Core Hosting Bundle first." }

# --- Step 1: validate the published folder ---
Log "=== Step 1: Validate published folder ===" "STEP"
if (-not (Test-Path $AppPath)) { Fail "AppPath not found: $AppPath" }
$dll = Join-Path $AppPath "TestController.WebApi.dll"
$spa = Join-Path $AppPath "wwwroot\index.html"
if (-not (Test-Path $dll)) { Fail "Not a published app: missing TestController.WebApi.dll in $AppPath" }
if (-not (Test-Path $spa)) { Log "wwwroot\index.html not found - the React SPA may be missing from this folder." "WARN" }
Log "Found published app in $AppPath" "OK"

# --- Step 2: production config (Controller upstream) ---
Log "=== Step 2: Write production config ===" "STEP"
$cfg = Write-ProductionConfig $AppPath $ControllerUrlConfigKey $ControllerUrl
Log "Set '$ControllerUrlConfigKey' = '$ControllerUrl' in $(Split-Path $cfg -Leaf)" "OK"
Log "CONFIRM '$ControllerUrlConfigKey' is the key TestController.WebApi reads for the Controller address." "WARN"

# --- Step 3: web.config + logs ---
Log "=== Step 3: Ensure web.config + logs ===" "STEP"
$webConfig = Join-Path $AppPath "web.config"
if (-not (Test-Path $webConfig)) {
    $exe = Join-Path $AppPath "TestController.WebApi.exe"
    if (Test-Path $exe) { $procPath = ".\TestController.WebApi.exe"; $procArgs = "" }
    else { $procPath = "dotnet"; $procArgs = ".\TestController.WebApi.dll" }
    $argAttr = if ($procArgs) { " arguments=`"$procArgs`"" } else { "" }
    $wc = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
    </handlers>
    <aspNetCore processPath="$procPath"$argAttr stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="InProcess" requestTimeout="04:00:00">
      <environmentVariables>
        <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
      </environmentVariables>
    </aspNetCore>
    <webSocket enabled="true" />
  </system.webServer>
</configuration>
"@
    Set-Content -Path $webConfig -Value $wc -Encoding UTF8
    Log "Created web.config (processPath: $procPath)" "OK"
} else {
    Log "web.config already present - leaving it as is" "OK"
}
$logsDir = Join-Path $AppPath "logs"
if (-not (Test-Path $logsDir)) { New-Item $logsDir -ItemType Directory -Force | Out-Null }

# --- Optional: WebSocket feature (OFF by default - THIS is the slow step that hung) ---
if ($EnsureWebSockets) {
    Log "=== Ensuring IIS WebSocket feature (this can be slow) ===" "STEP"
    try {
        if (Get-Command Enable-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
            Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart -ErrorAction Stop | Out-Null
            Log "WebSocket feature enabled" "OK"
        } elseif (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
            Install-WindowsFeature Web-WebSockets -ErrorAction Stop | Out-Null
            Log "WebSocket feature installed" "OK"
        } else { Log "Could not determine how to install the WebSocket feature on this OS." "WARN" }
    } catch { Log "WebSocket feature step failed: $($_.Exception.Message)" "WARN" }
} else {
    Log "Skipping WebSocket feature check (pass -EnsureWebSockets to install it; SignalR works without it via long-polling)." "INFO"
}

# --- Step 4: IIS app pool + site ---
Log "=== Step 4: Configure IIS ===" "STEP"
Start-Service WAS -ErrorAction SilentlyContinue
Start-Service W3SVC -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# Remove existing site/pool for a clean state
if (& $appcmd list site /name:"$SiteName" 2>$null) { & $appcmd delete site /site.name:"$SiteName" 2>$null | Out-Null }
if (& $appcmd list apppool /name:"$SiteName" 2>$null) {
    & $appcmd stop apppool /apppool.name:"$SiteName" 2>$null | Out-Null
    Start-Sleep -Seconds 1
    & $appcmd delete apppool /apppool.name:"$SiteName" 2>$null | Out-Null
}

# App pool (No Managed Code) + warm settings (keep SignalR + proxy alive)
& $appcmd add apppool /name:"$SiteName" /managedRuntimeVersion:"" 2>$null | Out-Null
& $appcmd set apppool /apppool.name:"$SiteName" /processModel.idleTimeout:"00:00:00" 2>$null | Out-Null
& $appcmd set apppool /apppool.name:"$SiteName" /startMode:"AlwaysRunning" 2>$null | Out-Null
& $appcmd set apppool /apppool.name:"$SiteName" /recycling.periodicRestart.time:"00:00:00" 2>$null | Out-Null
Log "App pool ready: $SiteName (No Managed Code, warm)" "OK"

# Site -> AppPath (host in place, no copy)
& $appcmd add site /name:"$SiteName" /physicalPath:"$AppPath" /bindings:"http/*:${SitePort}:" 2>$null | Out-Null
& $appcmd set site /site.name:"$SiteName" /[path='/'].applicationPool:"$SiteName" 2>$null | Out-Null
& $appcmd set site /site.name:"$SiteName" /serverAutoStart:"true" 2>$null | Out-Null
Log "Site ready: $SiteName -> $AppPath (port $SitePort)" "OK"

# Permissions
icacls $AppPath /grant "IIS AppPool\${SiteName}:(OI)(CI)RX" /T /Q 2>$null | Out-Null
icacls $logsDir /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T /Q 2>$null | Out-Null
Log "Permissions set" "OK"

# Firewall (non-fatal)
try {
    Remove-NetFirewallRule -DisplayName "TestControllerWeb" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "TestControllerWeb" -Direction Inbound -Protocol TCP -LocalPort $SitePort -Action Allow -Profile Any -ErrorAction Stop | Out-Null
    Log "Firewall: port $SitePort open" "OK"
} catch { Log "Could not set firewall rule for port $SitePort - open it manually if the site is unreachable." "WARN" }

# Start
& $appcmd start apppool /apppool.name:"$SiteName" 2>$null | Out-Null
& $appcmd start site /site.name:"$SiteName" 2>$null | Out-Null
Start-Sleep -Seconds 3
Log "IIS site started" "OK"

# --- Step 5: validate ---
Log "=== Step 5: Validate ===" "STEP"
$testUrl = "http://localhost:$SitePort"

$staticOk = $false
for ($i = 1; $i -le 6; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri $testUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $staticOk = $true; break }
    } catch { if ($i -lt 6) { Start-Sleep -Seconds 4 } }
}
if ($staticOk) { Log "Static SPA serving: HTTP 200 at $testUrl" "OK" }
else {
    Log "Static SPA NOT serving at $testUrl (may still be warming up)" "WARN"
    Log "  Check Event Viewer > Windows Logs > Application, and $AppPath\logs\stdout*" "WARN"
}

$healthUrl = "$testUrl$HealthPath"
$healthOk = $false
try { $h = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop; if ($h.StatusCode -eq 200) { $healthOk = $true } } catch { }
if ($healthOk) { Log "WebAPI alive: GET $HealthPath -> 200" "OK" }
else { Log "WebAPI health probe failed at $healthUrl (confirm $HealthPath exists and is anonymous)" "WARN" }

if (Test-TcpPort $ctrlHost $ctrlPort 3000) { Log "WPF Controller reachable at ${ctrlHost}:${ctrlPort}" "OK" }
else {
    $bmsg = "WPF Controller NOT reachable at ${ctrlHost}:${ctrlPort}. The SPA loads, but every /api call that needs data/auth fails until the Controller is running."
    if ($RequireBackend) { Fail $bmsg } else { Log $bmsg "WARN" }
}

# --- Summary ---
$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "===========================================================" -ForegroundColor Green
Write-Host "  DEPLOY COMPLETE ($($elapsed.ToString('mm\:ss')))" -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Green
Write-Host "  URL:        $testUrl" -ForegroundColor White
Write-Host "  Health:     $testUrl$HealthPath" -ForegroundColor White
Write-Host "  App folder: $AppPath" -ForegroundColor Gray
Write-Host "  Controller: $ControllerUrl  (key: $ControllerUrlConfigKey)" -ForegroundColor White
Write-Host ""
Write-Host "  The WebAPI owns no database - it proxies to the WPF Controller above," -ForegroundColor Gray
Write-Host "  which MUST be running for the site to work." -ForegroundColor Gray
Write-Host ""
