#Requires -RunAsAdministrator
<#
.SYNOPSIS
    TestAgent AGENT Node - setup, firewall, WinRM, HTTP/2 endpoint and launch registration.

.DESCRIPTION
    Run on each AGENT machine. Configures Windows Firewall (all profiles), removes any
    auto-created Block rule for testagentgrpc.exe, enables and verifies WinRM, validates
    the .NET 10.0.12 runtime, reserves the HTTP.sys URL, generates an appsettings.json
    whose Kestrel endpoint is explicitly bound to HTTP/2 (required for gRPC over
    plaintext h2c), optionally installs TestAgentGrpc as a Windows service, and
    verifies connectivity back to the Controller.

    Supports -WhatIf: every change is announced and nothing is written.

    NOTE ON LAUNCH MODE: the production fleet runs the agent under the scheduled task
    "TestAgentGrpc Interactive", not as a Windows service. -InstallAsService is retained
    for standalone boxes only. Use Configure-AgentTaskRecovery.ps1 for the task-based fleet.

.PARAMETER VerifyEndpoint
    After setup, probe the local agent port with curl to confirm the endpoint
    actually negotiates HTTP/2 (catches the HTTP_1_1_REQUIRED / 0xd condition).

.PARAMETER SkipWinRM
    Leave the WinRM service and its firewall rules untouched.

.EXAMPLE
    .\Setup-AgentNode.ps1 -ControllerAddress http://JVGR22:5100 -WhatIf
.EXAMPLE
    .\Setup-AgentNode.ps1 -ControllerAddress http://JVGR22:5100 -VerifyEndpoint

.NOTES
    Target framework : .NET 10 (LTS) - runtime 10.0.12 or newer required
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 4.0  |  October 2026
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]    $AgentPort         = 5200,
    [string] $AgentName         = $env:COMPUTERNAME,
    [string] $ControllerAddress = "",                       # e.g. "http://JVGR22:5100"
    [int]    $ControllerPort    = 5100,
    [string] $InstallDir        = "C:\TestAgentService",
    [int]    $HeartbeatSeconds  = 15,
    [switch] $InstallAsService,
    [string] $ServiceUser       = "LocalSystem",            # or "DOMAIN\user"
    [pscredential] $ServiceCredential,                      # required for a non-LocalSystem account; prompts if omitted
    [switch] $SkipFirewall,
    [switch] $SkipWinRM,
    [switch] $SkipDotNetCheck,
    [switch] $VerifyEndpoint,
    [string] $LogFile           = "",
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Minimum shared-framework build the published agent binaries require. The
# solution's Microsoft.* package references are on 10.0.12 (security patches
# GHSA-2p3q-h3hg-jcqq / GHSA-8prm-248r-h957), so an older 10.x runtime must not
# be treated as satisfying the requirement. Bump here when the packages move.
$script:DotNetMinVersion = [version]'10.0.12'
$ServiceName             = 'TestAgentGrpc'

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

function Get-SharedFxVersion {
    # Newest installed build of a shared framework, or $null when absent.
    param([string[]]$Runtimes, [string]$Framework)
    # Captures the numeric part only, so a '-preview' build compares as its base version.
    $pattern = '^{0}\s+(\d+\.\d+\.\d+)' -f [regex]::Escape($Framework)
    @($Runtimes | ForEach-Object { if ($_ -match $pattern) { [version]$Matches[1] } }) |
        Sort-Object -Descending | Select-Object -First 1
}

function New-FirewallRuleIfMissing {
    param([hashtable]$Params)
    $existing = Get-NetFirewallRule -DisplayName $Params.DisplayName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Note "Already exists: $($Params.DisplayName)"
        return
    }
    if (-not (Test-ShouldChange "Create firewall rule '$($Params.DisplayName)'")) { return }
    New-NetFirewallRule @Params -Enabled True -Profile Any | Out-Null
    Write-Ok "Created: $($Params.DisplayName)"
}

function Test-ShouldChange {
    # $WhatIfPreference is set by -WhatIf and inherited by called functions.
    param([string]$Description)
    if ($WhatIfPreference) { Write-Host "  [WHATIF] $Description" -ForegroundColor Magenta; return $false }
    return $true
}

function Remove-AgentBlockRule {
    <#
        Removes enabled Block rules whose application filter names testagentgrpc.exe.
        Deliberately narrow: only Action=Block, only that executable. Every removal is
        logged. A rule is never removed just because its DISPLAY NAME mentions the agent.
    #>
    $removed = 0
    try {
        $blocks = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Block' })
    } catch {
        Write-Warn "Could not enumerate firewall rules: $($_.Exception.Message)"
        return 0
    }
    foreach ($r in $blocks) {
        $app = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
        if (-not ($app -and $app.Program -and $app.Program -match 'testagentgrpc\.exe')) { continue }
        Write-Warn ("Block rule found: '{0}' profile={1} dir={2} program={3}" -f $r.DisplayName, $r.Profile, $r.Direction, $app.Program)
        if (-not (Test-ShouldChange "Remove Block rule '$($r.DisplayName)'")) { continue }
        try {
            Remove-NetFirewallRule -Name $r.Name -ErrorAction Stop
            Write-Ok ("Removed Block rule: '{0}' (program {1})" -f $r.DisplayName, $app.Program)
            $removed++
        } catch {
            Write-Err ("Failed to remove Block rule '{0}': {1}" -f $r.DisplayName, $_.Exception.Message)
        }
    }
    if ($removed -eq 0) { Write-Ok 'No Block rules for testagentgrpc.exe present.' }
    return $removed
}

if ($LogFile) {
    try { Start-Transcript -Path $LogFile -Append -ErrorAction Stop | Out-Null } catch { }
}

Write-Host ""
Write-Host "  ========================================================" -ForegroundColor Green
Write-Host "   TestAgent AGENT Node Setup"                              -ForegroundColor Green
Write-Host ("   Name: {0} | Port: {1} | .NET {2}+" -f $AgentName, $AgentPort, $script:DotNetMinVersion) -ForegroundColor Green
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

$total   = 10
$hadWarn = $false

# --- 1. Administrator -------------------------------------------------------
Write-Step 1 $total "Verifying Administrator privileges..."
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Err "Run this script from an elevated (Administrator) prompt."; exit 1 }
Write-Ok "Running as Administrator"

# --- 2. .NET runtime (minimum patch build enforced) -------------------------
Write-Host ""
$minNet = $script:DotNetMinVersion
Write-Step 2 $total "Checking .NET runtime (minimum $minNet)..."
if ($SkipDotNetCheck) {
    Write-Note "Skipped (-SkipDotNetCheck)"
} else {
    $url = "https://dotnet.microsoft.com/download/dotnet/{0}.{1}" -f $minNet.Major, $minNet.Minor
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "'dotnet' not found on PATH. Install the .NET $minNet ASP.NET Core and Windows Desktop runtimes."
        Write-Cyan $url
        exit 1
    }
    $runtimes = @(& dotnet --list-runtimes 2>$null)

    foreach ($fx in @(
        @{ Name = 'Microsoft.AspNetCore.App';     Label = 'ASP.NET Core';    Purpose = 'Kestrel/gRPC server' },
        @{ Name = 'Microsoft.WindowsDesktop.App'; Label = 'Windows Desktop'; Purpose = 'WinForms tray icon'  }
    )) {
        $have = Get-SharedFxVersion -Runtimes $runtimes -Framework $fx.Name
        if (-not $have) {
            Write-Warn "$($fx.Label) runtime NOT found - required for the $($fx.Purpose). Install $($fx.Name) $minNet or newer."
            Write-Cyan $url
            $hadWarn = $true
        } elseif ($have -lt $minNet) {
            Write-Warn "$($fx.Label) $have is BELOW the required $minNet - update this node before deploying the agent."
            Write-Cyan $url
            $hadWarn = $true
        } else {
            Write-Ok "$($fx.Label) $have found ($($fx.Purpose))"
        }
    }
}

# --- 3. Directories ---------------------------------------------------------
Write-Host ""
Write-Step 3 $total "Creating directories..."
foreach ($d in @($InstallDir, (Join-Path $InstallDir 'Logs'))) {
    if (Test-Path $d) { Write-Note "Exists:  $d" }
    elseif (Test-ShouldChange "Create directory $d") { New-Item -Path $d -ItemType Directory -Force | Out-Null; Write-Ok "Created: $d" }
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
Write-Step 5 $total "Removing Block rules for the agent executable..."
if ($SkipFirewall) {
    Write-Note "Skipped (-SkipFirewall)"
} else {
    $null = Remove-AgentBlockRule
}

# --- 6. WinRM ---------------------------------------------------------------
Write-Host ""
Write-Step 6 $total "Enabling and verifying WinRM..."
if ($SkipWinRM) {
    Write-Note "Skipped (-SkipWinRM)"
} else {
    $svc = Get-Service -Name WinRM -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Warn "WinRM service not present on this machine."
        $hadWarn = $true
    } else {
        $startMode = (Get-CimInstance Win32_Service -Filter "Name='WinRM'" -ErrorAction SilentlyContinue).StartMode
        Write-Note "Current: status=$($svc.Status) start=$startMode"

        if ($svc.Status -ne 'Running' -or $startMode -notmatch '^Auto') {
            if (Test-ShouldChange 'Enable PS remoting (WinRM service Automatic + started + listener + firewall rules)') {
                try {
                    # -SkipNetworkProfileCheck stops this failing when an adapter sits on the
                    # Public profile. It still scopes the Public rule to LocalSubnet.
                    Enable-PSRemoting -Force -SkipNetworkProfileCheck -ErrorAction Stop | Out-Null
                    Set-Service -Name WinRM -StartupType Automatic -ErrorAction Stop
                    Write-Ok "WinRM enabled (Automatic, started)"
                } catch {
                    Write-Warn "Enable-PSRemoting failed: $($_.Exception.Message)"
                    $hadWarn = $true
                }
            }
        } else {
            Write-Ok "WinRM already Running and Automatic"
        }

        foreach ($r in @(Get-NetFirewallRule -DisplayName 'Windows Remote Management (HTTP-In)' -ErrorAction SilentlyContinue)) {
            if ($r.Enabled -eq 'True') { Write-Note "FW already enabled [$($r.Profile)] $($r.DisplayName)" }
            elseif (Test-ShouldChange "Enable firewall rule [$($r.Profile)] $($r.DisplayName)") {
                try { Enable-NetFirewallRule -Name $r.Name -ErrorAction Stop; Write-Ok "FW enabled [$($r.Profile)] $($r.DisplayName)" }
                catch { Write-Warn "Could not enable rule [$($r.Profile)]: $($_.Exception.Message)"; $hadWarn = $true }
            }
        }

        if (-not $WhatIfPreference) {
            try { $null = Test-WSMan -ComputerName localhost -ErrorAction Stop; Write-Ok "Test-WSMan OK" }
            catch { Write-Warn "Test-WSMan failed: $($_.Exception.Message)"; $hadWarn = $true }
        }
    }
}

# --- 7. HTTP.sys URL reservation (locale-independent Everyone SID) ----------
Write-Host ""
Write-Step 7 $total "Configuring HTTP.sys URL reservation..."
$urlAcl = netsh http show urlacl url=http://+:$AgentPort/ 2>&1 | Out-String
if ($urlAcl -match 'Reserved URL') {
    Write-Note "Already reserved: http://+:$AgentPort/"
} elseif (Test-ShouldChange "Reserve http://+:$AgentPort/") {
    $everyone = (New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')
                ).Translate([System.Security.Principal.NTAccount]).Value
    netsh http add urlacl url=http://+:$AgentPort/ user="$everyone" | Out-Null
    Write-Ok "Reserved http://+:$AgentPort/ for '$everyone'"
}

# --- 8. appsettings.json (with explicit Kestrel HTTP/2 endpoint) ------------
Write-Host ""
Write-Step 8 $total "Generating appsettings.json (Kestrel HTTP/2 enabled)..."
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
            AgentEndpoint                    = "http://${AgentName}:$AgentPort"
            RegistrationRetryCount           = 3
            RegistrationRetryIntervalSeconds = 30
            HeartbeatIntervalSeconds         = $HeartbeatSeconds
            MaxExecutionHistoryCount         = 200
            MaxOutputLinesPerExecution       = 5000
            CollectSystemMetrics             = $true
            # Timeout chain. A WatchList action Timeout of 0 falls back to this, so a
            # week-long test run needs a ceiling well above the old 120-minute default.
            MaxExecutionTimeoutMinutes       = 20160
            WatchdogGraceMinutes             = 60
        }
        AgentKestrel = [ordered]@{
            KeepAliveTimeoutMinutes = 20160
        }
        AuditSettings = [ordered]@{
            Enabled         = $true
            LogDirectory    = (Join-Path $InstallDir 'Logs\audit')
            RetentionDays   = 30
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
    if (Test-ShouldChange "Write $appSettingsPath") {
        [System.IO.File]::WriteAllText($appSettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Ok "Created: $appSettingsPath"
    }
    if (-not $ControllerAddress) {
        Write-Warn "Edit this file and set AgentSettings.ControllerAddress before starting the agent."
        $hadWarn = $true
    }
}

# --- 9. Windows service (optional; the fleet uses a scheduled task) ---------
Write-Host ""
Write-Step 9 $total "Windows service installation..."
if (-not $InstallAsService) {
    Write-Note "Skipped. The production fleet runs the agent under the scheduled task"
    Write-Note "'TestAgentGrpc Interactive' - see Configure-AgentTaskRecovery.ps1."
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
            # Never accept a plaintext password. A SecureString-backed PSCredential must be
            # supplied up front; this script stays non-interactive by design.
            $cred = $ServiceCredential
            if (-not $cred) {
                Write-Err "-ServiceCredential is required when -ServiceUser is not LocalSystem. Pass a PSCredential; this script does not prompt."
                exit 1
            }
            if (Test-ShouldChange "Create service '$ServiceName' as $ServiceUser") { New-Service @common -Credential $cred | Out-Null }
        } elseif (Test-ShouldChange "Create service '$ServiceName' as LocalSystem") {
            New-Service @common | Out-Null
        }
        if (-not $WhatIfPreference) {
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
}

# --- 10. Validation & connectivity ------------------------------------------
Write-Host ""
Write-Step 10 $total "Validation..."

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
Write-Host (" .NET Runtime: {0}+ (ASP.NET Core + Windows Desktop)" -f $script:DotNetMinVersion)
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
