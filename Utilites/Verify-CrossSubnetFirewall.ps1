<#
.SYNOPSIS
  Pre-submission evidence collector for the cross-subnet firewall request.
  Run this to CLOSE any gaps the network team might use to bounce the ticket
  back. Produces a clean evidence block you can paste into the request.

.DESCRIPTION
  Confirms the failure is inter-subnet TCP filtering and NOT a host-firewall
  issue on either end. Run it on both hosts: it runs Section A when this machine
  owns -AgentIp and Section B when it owns -ControllerIp.

.PARAMETER ControllerIp
  Controller address. Default: 10.48.190.248 (JVGR22)

.PARAMETER AgentIp
  Agent address. Default: 10.48.220.132 (W25s22Grr1)

.PARAMETER ControllerPort
  Controller gRPC port. Default: 5100

.PARAMETER AgentPort
  Agent gRPC port. Default: 5200

.PARAMETER WinRmPort
  WinRM port tested from the agent to show all TCP is blocked. Default: 5985

.EXAMPLE
  .\Verify-CrossSubnetFirewall.ps1
  Defaults: JVGR22 <-> W25s22Grr1.

.EXAMPLE
  .\Verify-CrossSubnetFirewall.ps1 -ControllerIp 10.48.190.248 -AgentIp 10.48.220.140

.NOTES
  Run on the AGENT for Section A and on the CONTROLLER for Section B.
#>

param(
    [string]$ControllerIp = "10.48.190.248",
    [string]$AgentIp      = "10.48.220.132",
    [int]$ControllerPort  = 5100,
    [int]$AgentPort       = 5200,
    [int]$WinRmPort       = 5985
)

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Cross-Subnet Firewall - Pre-Submission Evidence" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

$localIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notmatch '^127\.|^169\.254\.' } | ForEach-Object { $_.IPAddress })
$thisIp = if ($localIps -contains $AgentIp) { $AgentIp } elseif ($localIps -contains $ControllerIp) { $ControllerIp } else { $localIps | Select-Object -First 1 }
Write-Host "Running on host with IP: $thisIp`n"

# ============================================================
# SECTION A - run on the AGENT (-AgentIp)
# ============================================================
if ($thisIp -eq $AgentIp) {
    Write-Host "=== SECTION A: AGENT-SIDE EVIDENCE ===" -ForegroundColor Yellow

    Write-Host "`n[A1] Forward path: agent -> controller port $ControllerPort"
    $fwd = Test-NetConnection $ControllerIp -Port $ControllerPort -WarningAction SilentlyContinue
    Write-Host "  TcpTestSucceeded: $($fwd.TcpTestSucceeded)  (expect False until fixed)"
    Write-Host "  PingSucceeded:    $($fwd.PingSucceeded)  (expect True - proves host reachable)"

    Write-Host "`n[A2] WinRM path: agent -> controller port $WinRmPort"
    $winrm = Test-NetConnection $ControllerIp -Port $WinRmPort -WarningAction SilentlyContinue
    Write-Host "  TcpTestSucceeded: $($winrm.TcpTestSucceeded)  (expect False - confirms ALL TCP blocked)"

    Write-Host "`n[A3] CRITICAL GAP-CLOSER: is the AGENT's OWN firewall allowing inbound $AgentPort ?"
    $inbound = Get-NetFirewallRule -Direction Inbound -Enabled True -ErrorAction SilentlyContinue |
        Where-Object {
            ($_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue).LocalPort -contains "$AgentPort"
        }
    if ($inbound) {
        Write-Host "  Found inbound rule(s) allowing $AgentPort :" -ForegroundColor Green
        $inbound | Select-Object DisplayName, Action, Profile | Format-Table -AutoSize
    } else {
        Write-Host "  WARNING: No explicit inbound allow rule for $AgentPort on THIS agent." -ForegroundColor Red
        Write-Host "  The reverse-path timeout could be partly host-firewall, not just" -ForegroundColor Red
        Write-Host "  the subnet ACL. Add a host rule (below) so the network team cannot" -ForegroundColor Red
        Write-Host "  bounce the ticket. Run:" -ForegroundColor Red
        Write-Host "    New-NetFirewallRule -DisplayName 'TestAgent gRPC In' -Direction Inbound -Protocol TCP -LocalPort $AgentPort -Action Allow" -ForegroundColor Gray
    }

    Write-Host "`n[A4] Confirm agent service is actually listening on $AgentPort"
    $listening = Get-NetTCPConnection -LocalPort $AgentPort -State Listen -ErrorAction SilentlyContinue
    if ($listening) {
        $listenPid = $listening.OwningProcess | Select-Object -First 1
        Write-Host "  Agent IS listening on $AgentPort (PID $listenPid)" -ForegroundColor Green
    } else {
        Write-Host "  Agent is NOT listening on $AgentPort - fix the agent before the ACL." -ForegroundColor Red
    }

    Write-Host "`n[A5] traceroute to controller (shows where packets die)"
    Test-NetConnection $ControllerIp -TraceRoute -WarningAction SilentlyContinue |
        Select-Object -ExpandProperty TraceRoute
}

# ============================================================
# SECTION B - run on the CONTROLLER (-ControllerIp)
# ============================================================
elseif ($thisIp -eq $ControllerIp) {
    Write-Host "=== SECTION B: CONTROLLER-SIDE EVIDENCE ===" -ForegroundColor Yellow

    Write-Host "`n[B1] Confirm controller is listening on $ControllerPort"
    $listening = Get-NetTCPConnection -LocalPort $ControllerPort -State Listen -ErrorAction SilentlyContinue
    if ($listening) {
        $procId = $listening.OwningProcess | Select-Object -First 1
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        Write-Host "  Listening on $ControllerPort, PID $procId ($($proc.Name))" -ForegroundColor Green
    } else {
        Write-Host "  NOT listening on $ControllerPort - fix the controller first." -ForegroundColor Red
    }

    Write-Host "`n[B2] Confirm controller inbound firewall allows $ControllerPort"
    $inbound = Get-NetFirewallRule -Direction Inbound -Enabled True -ErrorAction SilentlyContinue |
        Where-Object {
            ($_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue).LocalPort -contains "$ControllerPort"
        }
    if ($inbound) {
        Write-Host "  Inbound allow rule(s) present:" -ForegroundColor Green
        $inbound | Select-Object DisplayName, Action, Profile, RemoteAddress | Format-Table -AutoSize
    } else {
        Write-Host "  No explicit inbound rule for $ControllerPort (may be Any/Allow globally)." -ForegroundColor Yellow
    }

    Write-Host "`n[B3] Forward path test from the controller perspective: -> agent port $AgentPort"
    $rev = Test-NetConnection $AgentIp -Port $AgentPort -WarningAction SilentlyContinue
    Write-Host "  TcpTestSucceeded: $($rev.TcpTestSucceeded)  (expect False until fixed)"
    Write-Host "  PingSucceeded:    $($rev.PingSucceeded)  (expect True)"

    Write-Host "`n[B4] traceroute to agent (shows where packets die)"
    Test-NetConnection $AgentIp -TraceRoute -WarningAction SilentlyContinue |
        Select-Object -ExpandProperty TraceRoute
}
else {
    Write-Host "This host ($($localIps -join ', ')) owns neither -AgentIp $AgentIp nor -ControllerIp $ControllerIp." -ForegroundColor Red
}

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host " Paste the relevant output into the firewall request as evidence." -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
