#Requires -RunAsAdministrator
<#
.SYNOPSIS
    TestAgent DISPLAY (Dashboard) Node - Setup & firewall.

.DESCRIPTION
    Run on the monitoring machine where TestAgentDisplay (WPF) runs. The Dashboard
    is read-only: it connects OUT to agents and never receives inbound connections,
    so only outbound firewall rules are required. Validates the .NET 10.0.12 Desktop
    runtime and tests reachability to any agents supplied via -AgentIPs.

.EXAMPLE
    .\Setup-DisplayNode.ps1 -AgentIPs 10.48.190.60,10.48.190.254

.NOTES
    Target framework : .NET 10 (LTS) - runtime 10.0.12 or newer required
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 3.1  |  October 2026
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]   $InstallDir = "C:\TestAgentService\Display",
    [int]      $AgentPort  = 5200,
    [string[]] $AgentIPs   = @(),
    [switch]   $SkipFirewall,
    [switch]   $SkipDotNetCheck,
    [string]   $LogFile    = "",
    [switch]   $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Must match the floor enforced by Setup-AgentNode.ps1.
$script:DotNetMinVersion = [version]'10.0.12'

function Write-Step($n, $t, $m) { Write-Host ("[{0}/{1}] {2}" -f $n, $t, $m) -ForegroundColor White }
function Write-Ok  ($m)         { Write-Host "  [ OK ] $m"  -ForegroundColor Green  }
function Write-Warn($m)         { Write-Host "  [WARN] $m"  -ForegroundColor Yellow }
function Write-Err ($m)         { Write-Host "  [FAIL] $m"  -ForegroundColor Red    }
function Write-Note($m)         { Write-Host "  $m"         -ForegroundColor Gray   }

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
Write-Host "  ========================================================" -ForegroundColor Magenta
Write-Host "   TestAgent DISPLAY (Dashboard) Node Setup"                -ForegroundColor Magenta
Write-Host ("   .NET {0}+ | Agent Port: {1}" -f $script:DotNetMinVersion, $AgentPort) -ForegroundColor Magenta
Write-Host "  ========================================================" -ForegroundColor Magenta
Write-Host ""

if ($Uninstall) {
    $rule = "TestAgent Display to Agents Outbound (TCP $AgentPort)"
    if (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -DisplayName $rule; Write-Ok "Removed: $rule"
    }
    Get-NetFirewallRule -DisplayName "TestAgent Display to Agent *" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-NetFirewallRule -DisplayName $_.DisplayName; Write-Ok "Removed: $($_.DisplayName)" }
    Write-Host "[UNINSTALL] Complete." -ForegroundColor Yellow
    if ($LogFile) { Stop-Transcript | Out-Null }
    exit 0
}

$total   = 5
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
Write-Step 2 $total "Checking .NET Desktop runtime (minimum $minNet)..."
if ($SkipDotNetCheck) {
    Write-Note "Skipped (-SkipDotNetCheck)"
} else {
    $url = "https://dotnet.microsoft.com/download/dotnet/{0}.{1}" -f $minNet.Major, $minNet.Minor
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "'dotnet' not found on PATH. Install the .NET $minNet Desktop Runtime."
        Write-Host "  $url" -ForegroundColor Cyan; exit 1
    }
    # Take the NEWEST installed build: nodes carry several side-by-side versions, so
    # stopping at the first matching line would accept a stale one.
    $have = @(& dotnet --list-runtimes 2>$null | ForEach-Object {
        if ($_ -match '^Microsoft\.WindowsDesktop\.App\s+(\d+\.\d+\.\d+)') { [version]$Matches[1] }
    }) | Sort-Object -Descending | Select-Object -First 1

    if (-not $have) {
        Write-Warn "Windows Desktop runtime NOT found - required for the WPF Dashboard."
        Write-Host "  $url" -ForegroundColor Cyan; $hadWarn = $true
    } elseif ($have -lt $minNet) {
        Write-Warn "Windows Desktop $have is BELOW the required $minNet - update this node."
        Write-Host "  $url" -ForegroundColor Cyan; $hadWarn = $true
    } else {
        Write-Ok "Windows Desktop $have found"
    }
}

# --- 3. Directory -----------------------------------------------------------
Write-Host ""
Write-Step 3 $total "Creating directory..."
if (Test-Path $InstallDir) { Write-Note "Exists:  $InstallDir" }
else { New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null; Write-Ok "Created: $InstallDir" }

# --- 4. Firewall (outbound only) -------------------------------------------
Write-Host ""
Write-Step 4 $total "Configuring Firewall (outbound to agents)..."
if ($SkipFirewall) {
    Write-Note "Skipped (-SkipFirewall)"
} else {
    New-FirewallRuleIfMissing @{
        DisplayName = "TestAgent Display to Agents Outbound (TCP $AgentPort)"
        Description = "Allow outbound gRPC to agents for SubscribeAgentEvents monitoring"
        Direction   = 'Outbound'; Protocol = 'TCP'; RemotePort = $AgentPort; Action = 'Allow'
    }
    foreach ($ip in $AgentIPs) {
        New-FirewallRuleIfMissing @{
            DisplayName   = "TestAgent Display to Agent $ip"
            Description   = "Explicit outbound allow to agent at $ip"
            Direction     = 'Outbound'; Protocol = 'TCP'; RemoteAddress = $ip; RemotePort = $AgentPort; Action = 'Allow'
        }
    }
}

# --- 5. Connectivity --------------------------------------------------------
Write-Host ""
Write-Step 5 $total "Testing agent connectivity..."
if ($AgentIPs.Count -eq 0) {
    Write-Note "No agent IPs supplied (use -AgentIPs to test reachability)"
} else {
    foreach ($ip in $AgentIPs) {
        $tcp = Test-NetConnection -ComputerName $ip -Port $AgentPort -WarningAction SilentlyContinue
        if ($tcp.TcpTestSucceeded) { Write-Ok "${ip}:$AgentPort reachable" }
        else { Write-Err "${ip}:$AgentPort NOT reachable"; $hadWarn = $true }
    }
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Magenta
Write-Host " DISPLAY SETUP COMPLETE"                                  -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host " NEXT STEPS:" -ForegroundColor Yellow
Write-Host "  1. Copy the published TestAgentDisplay binaries to: $InstallDir"
Write-Host "  2. Run: $InstallDir\TestAgentDisplay.exe"
Write-Host "  3. Add agents in the Dashboard UI: http://AGENT_IP:$AgentPort"
Write-Host ""

if ($LogFile) { Stop-Transcript | Out-Null }
if ($hadWarn) { exit 2 } else { exit 0 }
