<#
.SYNOPSIS
    TestAgent Network Diagnostic - test connectivity between all nodes.

.DESCRIPTION
    Run on ANY machine to test reachability to the Controller and Agent nodes.
    Does NOT require Administrator. Returns exit code 0 when every tested
    endpoint passes, 1 when one or more fail, so a batch wrapper can branch on it.

.EXAMPLE
    .\Test-TestAgentNetwork.ps1 -ControllerIP 10.108.46.171 -AgentIPs 10.228.117.101,10.228.117.102 -ExportReport

.NOTES
    Compatibility : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version       : 3.0  |  May 2026
#>

[CmdletBinding()]
param(
    [string]   $ControllerIP   = "",
    [int]      $ControllerPort  = 5100,
    [string[]] $AgentIPs        = @(),
    [int]      $AgentPort       = 5200,
    [switch]   $ExportReport
)

$ErrorActionPreference = 'Continue'

function Get-LocalIPv4Obj {
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InterfaceAlias -notmatch 'Loopback' -and
            $_.IPAddress      -notmatch '^169\.254\.' -and
            ($_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual')
        }
}

Write-Host ""
Write-Host "  ========================================================" -ForegroundColor Yellow
Write-Host "   TestAgent Network Diagnostic Tool"                       -ForegroundColor Yellow
Write-Host "  ========================================================" -ForegroundColor Yellow
Write-Host ""

# --- Local machine ----------------------------------------------------------
Write-Host "[LOCAL MACHINE]" -ForegroundColor Cyan
Write-Host "  Hostname:  $env:COMPUTERNAME" -ForegroundColor White
$localIPs = @(Get-LocalIPv4Obj)
foreach ($lip in $localIPs) {
    Write-Host ("  IPv4:      {0} ({1}, /{2})" -f $lip.IPAddress, $lip.InterfaceAlias, $lip.PrefixLength) -ForegroundColor White
}
$primaryLocalIP = if ($localIPs.Count -gt 0) { $localIPs[0].IPAddress } else { '(none)' }
$dns = Get-DnsClientGlobalSetting -ErrorAction SilentlyContinue
if ($dns) { Write-Host ("  DNS Suffix: {0}" -f ($dns.SuffixSearchList -join ', ')) -ForegroundColor Gray }

$report = @()

function Test-Endpoint {
    param([string]$Name, [string]$Target, [int]$Port, [string]$Role)

    $r = [PSCustomObject]@{
        Name = $Name; Target = $Target; Port = $Port; Role = $Role
        DNS = 'N/A'; Ping = 'N/A'; PingMs = 'N/A'; TCP = 'N/A'; IPv6Issue = $false
    }

    # DNS (only when a hostname was supplied)
    if ($Target -match '^[A-Za-z]') {
        Write-Host "    DNS Lookup: " -NoNewline -ForegroundColor Gray
        try {
            $addrs = [System.Net.Dns]::GetHostAddresses($Target)
            $v4 = $addrs | Where-Object { $_.AddressFamily -eq 'InterNetwork' }
            $v6 = $addrs | Where-Object { $_.AddressFamily -eq 'InterNetworkV6' }
            if ($v4) {
                $r.DNS = "OK ($($v4[0]))"; Write-Host "$($v4[0])" -ForegroundColor Green
            } elseif ($v6) {
                $r.DNS = "IPv6 ONLY ($($v6[0]))"; $r.IPv6Issue = $true
                Write-Host "$($v6[0]) (IPv6 only)" -ForegroundColor Yellow
                Write-Host "           WARNING: resolves to IPv6 only; use the IPv4 address instead." -ForegroundColor Yellow
            } else {
                $r.DNS = 'FAILED'; Write-Host 'FAILED' -ForegroundColor Red
            }
        } catch {
            $r.DNS = "FAILED: $($_.Exception.Message)"; Write-Host 'FAILED' -ForegroundColor Red
        }
    }

    # Ping
    Write-Host "    Ping:       " -NoNewline -ForegroundColor Gray
    $p = Test-NetConnection -ComputerName $Target -WarningAction SilentlyContinue
    if ($p.PingSucceeded) {
        $ms = $p.PingReplyDetails.RoundtripTime
        $r.Ping = 'OK'; $r.PingMs = "${ms}ms"
        Write-Host "OK (${ms}ms)" -ForegroundColor Green
    } else {
        $r.Ping = 'FAILED'; Write-Host 'FAILED' -ForegroundColor Red
    }

    # TCP
    Write-Host ("    TCP {0}:  " -f $Port) -NoNewline -ForegroundColor Gray
    $t = Test-NetConnection -ComputerName $Target -Port $Port -WarningAction SilentlyContinue
    if ($t.TcpTestSucceeded) {
        $r.TCP = 'OPEN'; Write-Host 'OPEN' -ForegroundColor Green
    } else {
        $r.TCP = 'BLOCKED/CLOSED'; Write-Host 'BLOCKED / CLOSED' -ForegroundColor Red
        if ($r.Ping -eq 'OK') {
            Write-Host "           Ping OK but TCP fails => firewall is blocking port $Port." -ForegroundColor Yellow
            Write-Host "           Ask IT to open TCP $Port from $primaryLocalIP to $Target." -ForegroundColor Yellow
        } else {
            Write-Host "           Ping and TCP both fail => host unreachable or wrong address." -ForegroundColor Yellow
        }
    }
    return $r
}

# --- Controller -------------------------------------------------------------
if ($ControllerIP) {
    Write-Host ""
    Write-Host "[CONTROLLER: ${ControllerIP}:$ControllerPort]" -ForegroundColor Cyan
    $report += Test-Endpoint -Name 'Controller' -Target $ControllerIP -Port $ControllerPort -Role 'Controller'
}

# --- Agents -----------------------------------------------------------------
foreach ($agentIP in $AgentIPs) {
    Write-Host ""
    Write-Host "[AGENT: ${agentIP}:$AgentPort]" -ForegroundColor Cyan
    $report += Test-Endpoint -Name 'Agent' -Target $agentIP -Port $AgentPort -Role 'Agent'
}

# --- Local firewall rules ---------------------------------------------------
Write-Host ""
Write-Host "[LOCAL FIREWALL RULES]" -ForegroundColor Cyan
$fwRules = Get-NetFirewallRule -DisplayName "TestAgent*" -ErrorAction SilentlyContinue
if ($fwRules) {
    foreach ($fw in $fwRules) {
        $enabled = ($fw.Enabled -eq 'True')
        $color   = if ($enabled) { 'Green' } else { 'Yellow' }
        $status  = if ($enabled) { 'ENABLED' } else { 'DISABLED' }
        Write-Host ("  [{0}] {1} ({2})" -f $status, $fw.DisplayName, $fw.Direction) -ForegroundColor $color
    }
} else {
    Write-Host "  No TestAgent firewall rules found (run the relevant Setup-*Node.ps1)." -ForegroundColor Yellow
}

# --- Local listening ports --------------------------------------------------
Write-Host ""
Write-Host "[LOCAL LISTENING PORTS]" -ForegroundColor Cyan
foreach ($checkPort in @($ControllerPort, $AgentPort)) {
    $listener = Get-NetTCPConnection -LocalPort $checkPort -State Listen -ErrorAction SilentlyContinue
    if ($listener) {
        $ownerPid = $listener[0].OwningProcess
        $proc     = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        Write-Host ("  Port {0}: LISTENING ({1}, PID {2})" -f $checkPort, $proc.ProcessName, $ownerPid) -ForegroundColor Green
    } else {
        Write-Host ("  Port {0}: not listening" -f $checkPort) -ForegroundColor Gray
    }
}

# --- Summary ----------------------------------------------------------------
Write-Host ""
Write-Host "========================================================" -ForegroundColor Yellow
Write-Host " DIAGNOSTIC SUMMARY"                                      -ForegroundColor Yellow
Write-Host "========================================================" -ForegroundColor Yellow
Write-Host ""

$failures = 0
foreach ($r in $report) {
    if ($r.TCP -eq 'OPEN') {
        Write-Host ("  [PASS] {0} {1}:{2} | Ping: {3} ({4}) | TCP: OPEN" -f $r.Role, $r.Target, $r.Port, $r.Ping, $r.PingMs) -ForegroundColor Green
    } else {
        $failures++
        Write-Host ("  [FAIL] {0} {1}:{2} | Ping: {3} ({4}) | TCP: {5}" -f $r.Role, $r.Target, $r.Port, $r.Ping, $r.PingMs, $r.TCP) -ForegroundColor Red
        if ($r.IPv6Issue) { Write-Host "         FIX: use the IPv4 address instead of the hostname." -ForegroundColor Yellow }
        if ($r.Ping -eq 'OK') { Write-Host "         FIX: a network firewall is blocking TCP $($r.Port). Contact IT." -ForegroundColor Yellow }
    }
}

if ($report.Count -eq 0) {
    Write-Host "  No endpoints tested. Supply -ControllerIP and/or -AgentIPs." -ForegroundColor Gray
} elseif ($failures -eq 0) {
    Write-Host ""
    Write-Host "  ALL CONNECTIVITY TESTS PASSED" -ForegroundColor Green
}

if ($ExportReport -and $report.Count -gt 0) {
    $reportPath = Join-Path (Get-Location) ("TestAgent_NetworkDiagnostic_{0}.csv" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
    $report | Export-Csv -Path $reportPath -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "  Report saved: $reportPath" -ForegroundColor Cyan
}

Write-Host ""
if ($failures -gt 0) { exit 1 } else { exit 0 }
