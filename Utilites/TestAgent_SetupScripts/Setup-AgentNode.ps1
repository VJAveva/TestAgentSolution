#Requires -RunAsAdministrator
<#
.SYNOPSIS
    TestAgent AGENT Node - Setup, firewall, HTTP/2 endpoint and (optional) service install.

.DESCRIPTION
    Run on each AGENT machine. Configures Windows Firewall, validates the
    .NET 10 runtime, reserves the HTTP.sys URL, generates an appsettings.json
    whose Kestrel endpoint is explicitly bound to HTTP/2 (required for gRPC over
    plaintext h2c), optionally installs TestAgentGrpc as a Windows service, and
    verifies connectivity back to the Controller.

.PARAMETER VerifyEndpoint
    After setup, probe the local agent port with curl to confirm the endpoint
    actually negotiates HTTP/2 (catches the HTTP_1_1_REQUIRED / 0xd condition).

.NOTES
    Target framework : .NET 10 (LTS)
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 3.0  |  May 2026
#>

[CmdletBinding()]
param(
    [int]    $AgentPort         = 5200,
    [string] $AgentName         = $env:COMPUTERNAME,
    [string] $ControllerAddress = "",                       # e.g. "http://JVGR22:5100"
    [int]    $ControllerPort    = 5100,
    [string] $InstallDir        = "C:\TestAgentSolution\Agent",
    [int]    $HeartbeatSeconds  = 15,
    [switch] $InstallAsService,
    [string] $ServiceUser       = "LocalSystem",            # or "DOMAIN\user"
    [pscredential] $ServiceCredential,                      # required for a non-LocalSystem account; prompts if omitted
    [switch] $SkipFirewall,
    [switch] $SkipDotNetCheck,
    [switch] $VerifyEndpoint,
    [string] $LogFile           = "",
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Major .NET version this solution targets. Bump here if you upgrade.
$script:DotNetMajor = 10
$ServiceName        = 'TestAgentGrpc'

# ----------------------------------------------------------------------------
#  Console helpers (ASCII-only so they render correctly under PS 5.1)
# ----------------------------------------------------------------------------
function Write-Step($n, $t, $m) { Write-Host ("[{0}/{1}] {2}" -f $n, $t, $m) -ForegroundColor White }
function Write-Ok  ($m)         { Write-Host "  [ OK ] $m"  -ForegroundColor Green  }
function Write-Warn($m)         { Write-Host "  [WARN] $m"  -ForegroundColor Yellow }
function Write-Err ($m)         { Write-Host "  [FAIL] $m"  -ForegroundColor Red    }
function Write-Note($m)         { Write-Host "  $m"         -ForegroundColor Gray   }
function Write-Cyan($m)         { Write-Host "  $m"         -ForegroundColor Cyan   }

function Get-LocalIPv4 {
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -notmatch '^127\.'      -and
            $_.IPAddress -notmatch '^169\.254\.' -and
            ($_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual')
        } | Select-Object -ExpandProperty IPAddress
}

function New-FirewallRuleIfMissing {
    param([hashtable]$Params)
    $existing = Get-NetFirewallRule -DisplayName $Params.DisplayName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Note "Already exists: $($Params.DisplayName)"
        return
    }
    New-NetFirewallRule @Params -Enabled True -Profile Any | Out-Null
    Write-Ok "Created: $($Params.DisplayName)"
}

if ($LogFile) {
    try { Start-Transcript -Path $LogFile -Append -ErrorAction Stop | Out-Null } catch { }
}

Write-Host ""
Write-Host "  ========================================================" -ForegroundColor Green
Write-Host "   TestAgent AGENT Node Setup"                              -ForegroundColor Green
Write-Host ("   Name: {0} | Port: {1} | .NET {2}" -f $AgentName, $AgentPort, $DotNetMajor) -ForegroundColor Green
Write-Host "  ========================================================" -ForegroundColor Green
Write-Host ""

# ----------------------------------------------------------------------------
#  Uninstall
# ----------------------------------------------------------------------------
if ($Uninstall) {
    Write-Host "[UNINSTALL] Removing service, firewall rules and URL reservation..." -ForegroundColor Yellow
    try {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
            sc.exe delete $ServiceName | Out-Null
            Write-Ok "Removed service: $ServiceName"
        }
        Get-Process -Name $ServiceName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        @(
            "TestAgent gRPC Inbound (TCP $AgentPort)",
            "TestAgent to Controller Outbound (TCP $ControllerPort)"
        ) | ForEach-Object {
            $r = Get-NetFirewallRule -DisplayName $_ -ErrorAction SilentlyContinue
            if ($r) { Remove-NetFirewallRule -DisplayName $_; Write-Ok "Removed firewall rule: $_" }
        }

        netsh http delete urlacl url=http://+:$AgentPort/ 2>$null | Out-Null
        Write-Host "`n[UNINSTALL] Complete. Files under '$InstallDir' were NOT removed." -ForegroundColor Yellow
        if ($LogFile) { Stop-Transcript | Out-Null }
        exit 0
    } catch {
        Write-Err "Uninstall error: $($_.Exception.Message)"
        if ($LogFile) { Stop-Transcript | Out-Null }
        exit 1
    }
}

$total   = 8
$hadWarn = $false

# --- 1. Administrator -------------------------------------------------------
Write-Step 1 $total "Verifying Administrator privileges..."
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Err "Run this script from an elevated (Administrator) prompt."; exit 1 }
Write-Ok "Running as Administrator"

# --- 2. .NET 10 runtime -----------------------------------------------------
Write-Host ""
Write-Step 2 $total "Checking .NET $DotNetMajor runtime..."
if ($SkipDotNetCheck) {
    Write-Note "Skipped (-SkipDotNetCheck)"
} else {
    $url = "https://dotnet.microsoft.com/download/dotnet/$DotNetMajor.0"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "'dotnet' not found on PATH. Install the .NET $DotNetMajor Desktop Runtime."
        Write-Cyan $url
        exit 1
    }
    $runtimes = & dotnet --list-runtimes 2>$null
    $aspnet   = $runtimes | Where-Object { $_ -match "Microsoft\.AspNetCore\.App $DotNetMajor\." }
    $desktop  = $runtimes | Where-Object { $_ -match "Microsoft\.WindowsDesktop\.App $DotNetMajor\." }

    if ($aspnet)  { Write-Ok "ASP.NET Core $DotNetMajor runtime found (Kestrel/gRPC server)" }
    else          { Write-Warn "ASP.NET Core $DotNetMajor runtime NOT found - required for the gRPC server."; Write-Cyan $url; $hadWarn = $true }

    if ($desktop) { Write-Ok ".NET $DotNetMajor Windows Desktop runtime found (system-tray UI)" }
    else          { Write-Warn ".NET $DotNetMajor Desktop runtime NOT found - needed for the WinForms tray icon."; $hadWarn = $true }
}

# --- 3. Directories ---------------------------------------------------------
Write-Host ""
Write-Step 3 $total "Creating directories..."
foreach ($d in @($InstallDir, (Join-Path $InstallDir 'Logs'))) {
    if (Test-Path $d) { Write-Note "Exists:  $d" }
    else { New-Item -Path $d -ItemType Directory -Force | Out-Null; Write-Ok "Created: $d" }
}

# --- 4. Firewall ------------------------------------------------------------
Write-Host ""
Write-Step 4 $total "Configuring Windows Firewall..."
if ($SkipFirewall) {
    Write-Note "Skipped (-SkipFirewall)"
} else {
    New-FirewallRuleIfMissing @{
        DisplayName = "TestAgent gRPC Inbound (TCP $AgentPort)"
        Description = "Allow inbound gRPC from Controller and Dashboard (command dispatch + monitoring)"
        Direction   = 'Inbound'; Protocol = 'TCP'; LocalPort = $AgentPort; Action = 'Allow'
    }
    New-FirewallRuleIfMissing @{
        DisplayName = "TestAgent to Controller Outbound (TCP $ControllerPort)"
        Description = "Allow outbound gRPC to Controller (registration, heartbeat, event push)"
        Direction   = 'Outbound'; Protocol = 'TCP'; RemotePort = $ControllerPort; Action = 'Allow'
    }
}

# --- 5. HTTP.sys URL reservation (locale-independent Everyone SID) ----------
Write-Host ""
Write-Step 5 $total "Configuring HTTP.sys URL reservation..."
$urlAcl = netsh http show urlacl url=http://+:$AgentPort/ 2>&1 | Out-String
if ($urlAcl -match 'Reserved URL') {
    Write-Note "Already reserved: http://+:$AgentPort/"
} else {
    $everyone = (New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')
                ).Translate([System.Security.Principal.NTAccount]).Value
    netsh http add urlacl url=http://+:$AgentPort/ user="$everyone" | Out-Null
    Write-Ok "Reserved http://+:$AgentPort/ for '$everyone'"
}

# --- 6. appsettings.json (with explicit Kestrel HTTP/2 endpoint) ------------
Write-Host ""
Write-Step 6 $total "Generating appsettings.json (Kestrel HTTP/2 enabled)..."
$appSettingsPath = Join-Path $InstallDir 'appsettings.json'
if (Test-Path $appSettingsPath) {
    Write-Note "Already exists: $appSettingsPath (not overwritten)"
} else {
    $ctrlAddr = if ($ControllerAddress) { $ControllerAddress } else { "http://CONTROLLER_IP_HERE:$ControllerPort" }
    $settings = [ordered]@{
        Kestrel = [ordered]@{
            # gRPC over plaintext requires HTTP/2. On a plaintext endpoint the
            # framework default (Http1AndHttp2) silently negotiates down to
            # HTTP/1.1 (no TLS/ALPN), so the Controller fails with
            # HTTP_1_1_REQUIRED (0xd). EndpointDefaults forces Http2 for every
            # endpoint WITHOUT declaring a Url here, so it will not collide with
            # the port the agent binds from AgentSettings.GrpcPort in code.
            EndpointDefaults = [ordered]@{ Protocols = 'Http2' }
        }
        AgentSettings = [ordered]@{
            AgentName                        = $AgentName
            GrpcPort                         = $AgentPort
            ControllerAddress                = $ctrlAddr
            AgentEndpoint                    = $null
            RegistrationRetryCount           = 3
            RegistrationRetryIntervalSeconds = 30
            HeartbeatIntervalSeconds         = $HeartbeatSeconds
            MaxExecutionHistoryCount         = 200
            MaxOutputLinesPerExecution       = 5000
            CollectSystemMetrics             = $true
        }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                'Default'              = 'Information'
                'Microsoft.AspNetCore' = 'Warning'
            }
        }
    }
    # Write UTF-8 WITHOUT BOM so the .NET config provider and editors stay happy.
    $json = $settings | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($appSettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Created: $appSettingsPath"
    if (-not $ControllerAddress) {
        Write-Warn "Edit this file and set AgentSettings.ControllerAddress before starting the agent."
        $hadWarn = $true
    }
}

# --- 7. Windows service (optional) ------------------------------------------
Write-Host ""
Write-Step 7 $total "Windows service installation..."
if (-not $InstallAsService) {
    Write-Note "Skipped (use -InstallAsService once the binaries are copied)"
} else {
    $exePath = Join-Path $InstallDir "$ServiceName.exe"
    if (-not (Test-Path $exePath)) {
        Write-Warn "$exePath not found. Copy the published binaries first, then re-run with -InstallAsService."
        $hadWarn = $true
    } elseif (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Note "Service already exists: $ServiceName"
    } else {
        $binPath = "`"$exePath`""   # quote in case the install path ever contains spaces
        $common  = @{
            Name        = $ServiceName
            DisplayName = 'TestAgent gRPC Service'
            Description = "TestAgent remote execution agent - gRPC HTTP/2 server on port $AgentPort"
            BinaryPathName = $binPath
            StartupType = 'Automatic'
        }
        if ($ServiceUser -ne 'LocalSystem') {
            # Never accept a plaintext password. Use a SecureString-backed PSCredential,
            # prompting interactively if one was not supplied on the command line.
            $cred = $ServiceCredential
            if (-not $cred) {
                $cred = Get-Credential -UserName $ServiceUser -Message "Credentials for the '$ServiceName' service account ($ServiceUser)"
            }
            if (-not $cred) { Write-Err "A credential is required when ServiceUser is not LocalSystem."; exit 1 }
            New-Service @common -Credential $cred | Out-Null
        } else {
            New-Service @common | Out-Null
        }
        # Delayed auto-start (network is ready) + auto-restart on failure.
        sc.exe config  $ServiceName start= delayed-auto | Out-Null
        # Escalating restart delays (5s, 10s, 30s); reset the failure counter
        # after 60s of healthy running.
        sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        # CRITICAL: treat NON-ZERO exit codes as failures so the watchdog's
        # Environment.Exit(3) triggers an SCM restart. Without this flag, a clean
        # exit(3) is treated as a normal stop and the agent is NOT restarted.
        sc.exe failureflag $ServiceName 1 | Out-Null
        Write-Ok "Installed service '$ServiceName' (delayed auto-start; restart 5s/10s/30s; non-zero exit = failure)"
        Write-Cyan "Start it with:  Start-Service $ServiceName"
    }
}

# --- 8. Validation & connectivity -------------------------------------------
Write-Host ""
Write-Step 8 $total "Validation..."

$portOwner = Get-NetTCPConnection -LocalPort $AgentPort -ErrorAction SilentlyContinue
if ($portOwner) {
    $ownerPid = $portOwner[0].OwningProcess
    $proc     = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    Write-Note "Port $AgentPort in use by $($proc.ProcessName) (PID $ownerPid)"
} else {
    Write-Ok "Port $AgentPort is free"
}

$localIPs = @(Get-LocalIPv4)
Write-Cyan "Agent IPv4 addresses: $($localIPs -join ', ')"

# Controller reachability (TCP)
if ($ControllerAddress -and $ControllerAddress -notmatch 'CONTROLLER_IP_HERE') {
    $uri = [System.Uri]$ControllerAddress
    $tcp = Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -WarningAction SilentlyContinue
    if ($tcp.TcpTestSucceeded) { Write-Ok "Controller reachable at $ControllerAddress" }
    else { Write-Warn "Cannot reach Controller at $ControllerAddress (is it running? is TCP $($uri.Port) open?)"; $hadWarn = $true }
}

# Optional: confirm the local endpoint negotiates HTTP/2 (needs the agent running)
if ($VerifyEndpoint) {
    Write-Host ""
    Write-Cyan "Probing local endpoint for HTTP/2 (h2c)..."
    $curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
    if (-not $curl) { $curl = Join-Path $env:SystemRoot 'System32\curl.exe' }
    if (Test-Path $curl) {
        $out = & $curl --http2-prior-knowledge -s -o NUL -w 'version=%{http_version};code=%{http_code}' --max-time 5 "http://localhost:$AgentPort/" 2>&1
        if ($LASTEXITCODE -eq 0 -and $out -match 'version=2') {
            Write-Ok "Endpoint speaks HTTP/2 - ready for gRPC ($out)"
        } else {
            Write-Warn "HTTP/2 probe did not confirm (exit $LASTEXITCODE): $out"
            Write-Note "If the agent is not started yet this is expected. Otherwise run Diagnose-Http2Protocol.ps1."
            $hadWarn = $true
        }
    } else {
        Write-Note "curl.exe unavailable - skipping HTTP/2 probe."
    }
}

# --- Summary ----------------------------------------------------------------
Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host " AGENT SETUP COMPLETE"                                    -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host (" Agent Name:   {0}" -f $AgentName)
Write-Host (" Agent Port:   {0} (HTTP/2)" -f $AgentPort)
Write-Host (" Install Dir:  {0}" -f $InstallDir)
Write-Host (" Agent IPs:    {0}" -f ($localIPs -join ', '))
Write-Host ""
Write-Host " NEXT STEPS:" -ForegroundColor Yellow
Write-Host "  1. Copy the published $ServiceName binaries to: $InstallDir"
Write-Host "  2. Confirm ControllerAddress in: $appSettingsPath"
Write-Host "  3. Ensure the agent binds the gRPC port as HTTP/2. The generated"
Write-Host "     appsettings.json sets Kestrel:EndpointDefaults:Protocols=Http2."
Write-Host "     If Program.cs binds the port in code, set the protocol explicitly:"
Write-Host "        builder.WebHost.ConfigureKestrel(o =>"                  -ForegroundColor Gray
Write-Host ("           o.ListenAnyIP({0}, lo => lo.Protocols = HttpProtocols.Http2));" -f $AgentPort) -ForegroundColor Gray
Write-Host "  4. Start the agent:  $InstallDir\$ServiceName.exe   (or: Start-Service $ServiceName)"
Write-Host "  5. Verify HTTP/2:    Setup-AgentNode.ps1 -VerifyEndpoint   (or Diagnose-Http2Protocol.ps1)"
Write-Host ""
Write-Host " Register this agent in the Controller as:" -ForegroundColor Yellow
foreach ($ip in $localIPs) { Write-Cyan "http://${ip}:$AgentPort" }
Write-Host ""

if ($LogFile) { Stop-Transcript | Out-Null }
if ($hadWarn) { exit 2 } else { exit 0 }
