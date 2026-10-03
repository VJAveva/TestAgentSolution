#Requires -RunAsAdministrator
<#
.SYNOPSIS
    TestAgent CONTROLLER Node - Setup, firewall and configuration.

.DESCRIPTION
    Run on the CONTROLLER machine. Configures Windows Firewall, validates the
    .NET 10 runtime (ASP.NET Core for the inbound gRPC server, Windows Desktop
    for the WPF UI), prepares directories, and generates appsettings.json plus a
    starter WatchList.xml. Per-agent outbound rules are added when -AgentIPs is
    supplied.

.NOTES
    Target framework : .NET 10 (LTS)
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 3.0  |  May 2026

    Reminder: the Controller's gRPC client must allow HTTP/2-over-plaintext for
    http:// agent endpoints. In the ControllerService startup, before the first
    GrpcChannel.ForAddress:
        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
#>

[CmdletBinding()]
param(
    [int]      $ControllerPort = 5100,
    [string]   $InstallDir     = "C:\TestAgentSolution\Controller",
    [string]   $VocabularyDir  = "C:\TestControllerService",
    [string[]] $AgentIPs       = @(),                 # e.g. @("10.228.117.101","10.228.117.102")
    [int]      $AgentPort      = 5200,
    [switch]   $SkipFirewall,
    [switch]   $SkipDotNetCheck,
    [string]   $LogFile        = "",
    [switch]   $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:DotNetMajor = 10

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
    if (Get-NetFirewallRule -DisplayName $Params.DisplayName -ErrorAction SilentlyContinue) {
        Write-Note "Already exists: $($Params.DisplayName)"; return
    }
    New-NetFirewallRule @Params -Enabled True -Profile Any | Out-Null
    Write-Ok "Created: $($Params.DisplayName)"
}

if ($LogFile) { try { Start-Transcript -Path $LogFile -Append -ErrorAction Stop | Out-Null } catch { } }

Write-Host ""
Write-Host "  ========================================================" -ForegroundColor Cyan
Write-Host "   TestAgent CONTROLLER Node Setup"                          -ForegroundColor Cyan
Write-Host ("   Port: {0} | .NET {1} | Install: {2}" -f $ControllerPort, $DotNetMajor, $InstallDir) -ForegroundColor Cyan
Write-Host "  ========================================================" -ForegroundColor Cyan
Write-Host ""

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    Write-Host "[UNINSTALL] Removing firewall rules..." -ForegroundColor Yellow
    $rules = @(
        "TestAgent Controller gRPC Inbound (TCP $ControllerPort)",
        "TestAgent Controller to Agents Outbound (TCP $AgentPort)"
    )
    $rules += (Get-NetFirewallRule -DisplayName "TestAgent Allow Agent *" -ErrorAction SilentlyContinue |
               Select-Object -ExpandProperty DisplayName)
    foreach ($r in ($rules | Select-Object -Unique)) {
        if (Get-NetFirewallRule -DisplayName $r -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -DisplayName $r; Write-Ok "Removed: $r"
        }
    }
    Write-Host "`n[UNINSTALL] Complete. Application files were NOT removed." -ForegroundColor Yellow
    if ($LogFile) { Stop-Transcript | Out-Null }
    exit 0
}

$total   = 7
$hadWarn = $false

# --- 1. Administrator -------------------------------------------------------
Write-Step 1 $total "Verifying Administrator privileges..."
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Err "Run this script from an elevated (Administrator) prompt."; exit 1 }
Write-Ok "Running as Administrator"

# --- 2. .NET 10 -------------------------------------------------------------
Write-Host ""
Write-Step 2 $total "Checking .NET $DotNetMajor runtime..."
if ($SkipDotNetCheck) {
    Write-Note "Skipped (-SkipDotNetCheck)"
} else {
    $url = "https://dotnet.microsoft.com/download/dotnet/$DotNetMajor.0"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "'dotnet' not found on PATH. Install the .NET $DotNetMajor Desktop Runtime."
        Write-Cyan $url; exit 1
    }
    $runtimes = & dotnet --list-runtimes 2>$null
    $desktop  = $runtimes | Where-Object { $_ -match "Microsoft\.WindowsDesktop\.App $DotNetMajor\." }
    $aspnet   = $runtimes | Where-Object { $_ -match "Microsoft\.AspNetCore\.App $DotNetMajor\." }
    if ($desktop) { Write-Ok ".NET $DotNetMajor Windows Desktop runtime found (WPF UI)" }
    else { Write-Warn ".NET $DotNetMajor Desktop runtime NOT found - required for the WPF Controller UI."; Write-Cyan $url; $hadWarn = $true }
    if ($aspnet)  { Write-Ok "ASP.NET Core $DotNetMajor runtime found (Kestrel gRPC server)" }
    else { Write-Warn "ASP.NET Core $DotNetMajor runtime NOT found - required for the inbound gRPC server."; Write-Cyan $url; $hadWarn = $true }
}

# --- 3. Directories ---------------------------------------------------------
Write-Host ""
Write-Step 3 $total "Creating directories..."
foreach ($d in @($InstallDir, $VocabularyDir, (Join-Path $VocabularyDir 'Logs'))) {
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
        DisplayName = "TestAgent Controller gRPC Inbound (TCP $ControllerPort)"
        Description = "Allow inbound gRPC from TestAgents (registration, heartbeat, event push)"
        Direction   = 'Inbound'; Protocol = 'TCP'; LocalPort = $ControllerPort; Action = 'Allow'
    }
    New-FirewallRuleIfMissing @{
        DisplayName = "TestAgent Controller to Agents Outbound (TCP $AgentPort)"
        Description = "Allow outbound gRPC to TestAgents (command dispatch)"
        Direction   = 'Outbound'; Protocol = 'TCP'; RemotePort = $AgentPort; Action = 'Allow'
    }
    foreach ($ip in $AgentIPs) {
        New-FirewallRuleIfMissing @{
            DisplayName  = "TestAgent Allow Agent $ip (TCP $AgentPort)"
            Description  = "Explicit outbound allow to agent at $ip"
            Direction    = 'Outbound'; Protocol = 'TCP'; RemoteAddress = $ip; RemotePort = $AgentPort; Action = 'Allow'
        }
    }
}

# --- 5. appsettings.json ----------------------------------------------------
Write-Host ""
Write-Step 5 $total "Generating appsettings.json template..."
$appSettingsPath = Join-Path $InstallDir 'appsettings.json'
if (Test-Path $appSettingsPath) {
    Write-Note "Already exists: $appSettingsPath (not overwritten)"
} else {
    $agents = @()
    if ($AgentIPs.Count -gt 0) {
        $i = 1
        foreach ($ip in $AgentIPs) {
            $agents += [ordered]@{ Name = "Agent$i"; Address = "http://${ip}:$AgentPort" }; $i++
        }
    } else {
        $agents += [ordered]@{ Name = 'Agent1'; Address = "http://AGENT_IP_HERE:$AgentPort" }
    }
    $settings = [ordered]@{
        VocabularyFile     = (Join-Path $VocabularyDir 'WatchList.xml')
        ControllerGrpcPort = $ControllerPort
        # Plaintext h2c clients require this switch in code (see header note).
        Http2UnencryptedSupport = $true
        Agents             = $agents
        Logging            = [ordered]@{
            LogLevel = [ordered]@{ 'Default' = 'Information'; 'Microsoft.AspNetCore' = 'Warning' }
        }
    }
    $json = $settings | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($appSettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Created: $appSettingsPath"
    if ($AgentIPs.Count -eq 0) { Write-Warn "Edit this file and set the real agent IPs."; $hadWarn = $true }
}

# --- 6. Starter WatchList.xml ----------------------------------------------
Write-Host ""
Write-Step 6 $total "Creating starter WatchList.xml..."
$watchListPath = Join-Path $VocabularyDir 'WatchList.xml'
if (Test-Path $watchListPath) {
    Write-Note "Already exists: $watchListPath (not overwritten)"
} else {
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<WatchList>
  <!-- Example WatchItem: watches C:\Triggers for renamed .txt files -->
  <WatchItem Tag="ExampleTrigger" Path="C:\Triggers" Filter="*.txt" IsEnabled="false">
    <Event Type="Renamed" ExecutionType="Sequential">
      <Action Type="RunCommand"
              Tag="LocalEcho"
              Command="cmd.exe"
              Parameters="/c echo Triggered by [FileName] at [DateTime]"
              Timeout="30"
              FailAndContinue="true" />
    </Event>
  </WatchItem>
  <Templates>
    <!-- Define reusable action groups here -->
  </Templates>
</WatchList>
"@
    [System.IO.File]::WriteAllText($watchListPath, $xml, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Created: $watchListPath"
}

# --- 7. Validation & summary ------------------------------------------------
Write-Host ""
Write-Step 7 $total "Validation..."
$portOwner = Get-NetTCPConnection -LocalPort $ControllerPort -ErrorAction SilentlyContinue
if ($portOwner) {
    $ownerPid = $portOwner[0].OwningProcess
    $proc     = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    Write-Warn "Port $ControllerPort already in use by $($proc.ProcessName) (PID $ownerPid)"
    $hadWarn = $true
} else {
    Write-Ok "Port $ControllerPort is free"
}
$fw = Get-NetFirewallRule -DisplayName "TestAgent*" -ErrorAction SilentlyContinue
if ($fw) { Write-Ok "Active TestAgent firewall rules: $(@($fw).Count)" }

$localIP = (Get-LocalIPv4 | Select-Object -First 1)
Write-Cyan "Controller IP: $localIP"

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " CONTROLLER SETUP COMPLETE"                               -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host (" Install Dir:     {0}" -f $InstallDir)
Write-Host (" Vocabulary Dir:  {0}" -f $VocabularyDir)
Write-Host (" Controller Port: {0}" -f $ControllerPort)
Write-Host (" Controller IP:   {0}" -f $localIP)
Write-Host ""
Write-Host " NEXT STEPS:" -ForegroundColor Yellow
Write-Host "  1. Copy the published TestControllerGrpc binaries to: $InstallDir"
Write-Host "  2. Set agent IPs in:       $appSettingsPath"
Write-Host "  3. Configure automation in: $watchListPath"
Write-Host "  4. Start: $InstallDir\TestControllerGrpc.exe"
Write-Host ""
Write-Host " Give this address to each agent (ControllerAddress):" -ForegroundColor Yellow
Write-Cyan "http://${localIP}:$ControllerPort"
Write-Host ""

if ($LogFile) { Stop-Transcript | Out-Null }
if ($hadWarn) { exit 2 } else { exit 0 }
