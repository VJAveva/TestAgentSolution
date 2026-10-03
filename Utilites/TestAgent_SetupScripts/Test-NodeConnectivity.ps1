<#
.SYNOPSIS
    TestAgent fleet connectivity and readiness diagnostic - PASS/FAIL per node.

.DESCRIPTION
    Read-only. Changes nothing on any node, so it needs no -WhatIf.

    Replaces Test-TestAgentNetwork.ps1 and Diagnose-AgentPort.ps1 (both archived).

    Checks per node:
      PING    ICMP reachability
      DNS     forward resolve, and reverse lookup agreeing with the name
      SMB     admin share reachable (also how the runtime check works without WinRM)
      APORT   agent gRPC port reachable from here
      WINRM   Test-WSMan answers
      C5100   node -> controller gRPC  (needs WinRM on the node; SKIP otherwise)
      C5200   node -> controller HTTP  (needs WinRM on the node; SKIP otherwise)
      FW      TestAgent Allow rules present AND no Block rule for testagentgrpc.exe
      NET     ASP.NET Core and Windows Desktop shared frameworks >= -MinDotNet
      REG     agent appears in the controller's registered-agent list

    Runtime versions are read from the shared-framework FOLDER NAMES over SMB, so a
    node with WinRM stopped is still measured rather than reported as unknown.

.PARAMETER Nodes
    Agent node names. Defaults to the agents in deploy\fleet-inventory.json when that
    file can be found, otherwise to the known fleet.

.PARAMETER ReportPath
    Optional path for a JSON copy of the results.

.EXAMPLE
    .\Test-NodeConnectivity.ps1
.EXAMPLE
    .\Test-NodeConnectivity.ps1 -Nodes JVGR1,WARMGR -Controller JVGR22
.EXAMPLE
    .\Test-NodeConnectivity.ps1 -ReportPath C:\Temp\fleet.json

.NOTES
    Target framework : .NET 10 (LTS) - runtime 10.0.12 or newer required
    Compatibility    : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version          : 1.0  |  October 2026
#>
[CmdletBinding()]
param(
    [string[]] $Nodes,
    [string]   $Controller         = 'JVGR22',
    [int]      $ControllerGrpcPort = 5100,
    [int]      $ControllerHttpPort = 5200,
    [int]      $AgentPort          = 5200,
    [version]  $MinDotNet          = [version]'10.0.12',
    [string]   $InventoryPath,
    [int]      $TimeoutSeconds     = 5,
    [string]   $ReportPath
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

# ---------------------------------------------------------------------------
#  Helpers
# ---------------------------------------------------------------------------
function New-Result {
    param([string]$State, [string]$Detail = '')
    [pscustomobject]@{ State = $State; Detail = $Detail }
}

function Resolve-NodeList {
    if ($Nodes -and $Nodes.Count) { return $Nodes }
    $candidates = @()
    if ($InventoryPath) { $candidates += $InventoryPath }
    $candidates += (Join-Path $PSScriptRoot 'fleet-inventory.json')
    $candidates += (Join-Path $PSScriptRoot '..\..\deploy\fleet-inventory.json')
    foreach ($c in $candidates) {
        try {
            if ($c -and (Test-Path -LiteralPath $c)) {
                $inv = Get-Content -LiteralPath $c -Raw | ConvertFrom-Json
                $names = @($inv.agents | ForEach-Object { $_.node })
                if ($names.Count) { return $names }
            }
        } catch { }
    }
    @('JVGR1','JVKPRI','JVKBAK','JVHIST','JVGR2','WARMGR','WARMPRI','WARMBAK','WARMHIST')
}

function Test-TcpPort {
    param([string]$ComputerName, [int]$Port, [int]$Timeout = 3000)
    $client = New-Object System.Net.Sockets.TcpClient
    try   { return $client.ConnectAsync($ComputerName, $Port).Wait($Timeout) }
    catch { return $false }
    finally { $client.Dispose() }
}

function Get-SharedFxVersion {
    # Newest installed build of a shared framework, read from folder names over SMB.
    param([string]$Node, [string]$Framework)
    $base = "\\$Node\C`$\Program Files\dotnet\shared\$Framework"
    try {
        if (-not (Test-Path -LiteralPath $base)) { return $null }
        $vers = Get-ChildItem -LiteralPath $base -Directory -ErrorAction Stop | ForEach-Object {
            $v = $null
            if ([version]::TryParse(($_.Name -replace '-.*$', ''), [ref]$v)) { $v }
        }
        return (@($vers) | Sort-Object -Descending | Select-Object -First 1)
    } catch { return $null }
}

function Get-RegisteredAgents {
    param([string]$ControllerHost, [int]$Port)
    $base = "http://${ControllerHost}:$Port"
    # /api/agents requires an authenticated user; default credentials usually satisfy
    # the existing NTLM/Negotiate path. Fall back to the anonymous /api/health probe.
    try {
        $r = Invoke-RestMethod -Uri "$base/api/agents" -UseDefaultCredentials -TimeoutSec 10 -ErrorAction Stop
        $names = @($r | ForEach-Object { $_.name; $_.agentName } | Where-Object { $_ })
        return @{ Ok = $true; Agents = $names; Note = 'api/agents' }
    } catch {
        try {
            $null = Invoke-RestMethod -Uri "$base/api/health" -TimeoutSec 10 -ErrorAction Stop
            return @{ Ok = $false; Agents = @(); Note = 'controller alive, api/agents not readable' }
        } catch {
            return @{ Ok = $false; Agents = @(); Note = 'controller API unreachable' }
        }
    }
}

# ---------------------------------------------------------------------------
#  Collect
# ---------------------------------------------------------------------------
$nodeList = Resolve-NodeList
Write-Host ''
Write-Host '  ========================================================' -ForegroundColor Cyan
Write-Host '   TestAgent Node Connectivity Diagnostic'                  -ForegroundColor Cyan
Write-Host ("   Controller: {0}  gRPC {1}  HTTP {2}  |  min .NET {3}" -f $Controller, $ControllerGrpcPort, $ControllerHttpPort, $MinDotNet) -ForegroundColor Cyan
Write-Host '  ========================================================' -ForegroundColor Cyan
Write-Host ''

$registry = Get-RegisteredAgents -ControllerHost $Controller -Port $ControllerHttpPort
Write-Host ("  Controller registry: {0}" -f $registry.Note) -ForegroundColor DarkGray
Write-Host ''

$rows = foreach ($node in $nodeList) {
    Write-Host ("  checking {0} ..." -f $node) -ForegroundColor DarkGray
    $c = [ordered]@{}

    $c.PING = if (Test-Connection -ComputerName $node -Count 1 -Quiet -ErrorAction SilentlyContinue) { New-Result 'PASS' } else { New-Result 'FAIL' 'no ICMP reply' }

    # DNS: forward must resolve; reverse should agree with the short name.
    try {
        $fwd = @(Resolve-DnsName -Name $node -Type A -ErrorAction Stop | Where-Object { $_.IPAddress })
        if ($fwd.Count) {
            $ip  = $fwd[0].IPAddress
            $rev = $null
            try { $rev = (Resolve-DnsName -Name $ip -Type PTR -ErrorAction Stop | Select-Object -First 1).NameHost } catch { }
            if ($rev -and ($rev -split '\.')[0] -ne $node) { $c.DNS = New-Result 'WARN' "fwd=$ip rev=$rev" }
            else { $c.DNS = New-Result 'PASS' $ip }
        } else { $c.DNS = New-Result 'FAIL' 'no A record' }
    } catch { $c.DNS = New-Result 'FAIL' $_.Exception.Message }

    $smbOk = Test-Path -LiteralPath "\\$node\C`$\" -ErrorAction SilentlyContinue
    $c.SMB = if ($smbOk) { New-Result 'PASS' } else { New-Result 'FAIL' 'admin share unreachable' }

    $c.APORT = if (Test-TcpPort -ComputerName $node -Port $AgentPort -Timeout ($TimeoutSeconds * 1000)) { New-Result 'PASS' "tcp/$AgentPort" } else { New-Result 'FAIL' "tcp/$AgentPort closed" }

    $wsman = $false
    try { $null = Test-WSMan -ComputerName $node -ErrorAction Stop; $wsman = $true } catch { }
    $c.WINRM = if ($wsman) { New-Result 'PASS' } else { New-Result 'FAIL' 'Test-WSMan failed' }

    # Reverse direction: only measurable from the node itself.
    if ($wsman) {
        try {
            $back = Invoke-Command -ComputerName $node -ErrorAction Stop -ScriptBlock {
                param($ctrl, $p1, $p2, $to)
                $probe = {
                    param($h, $p, $t)
                    $cl = New-Object System.Net.Sockets.TcpClient
                    try { $cl.ConnectAsync($h, $p).Wait($t * 1000) } catch { $false } finally { $cl.Dispose() }
                }
                [pscustomobject]@{
                    P1 = & $probe $ctrl $p1 $to
                    P2 = & $probe $ctrl $p2 $to
                }
            } -ArgumentList $Controller, $ControllerGrpcPort, $ControllerHttpPort, $TimeoutSeconds
            $c.C5100 = if ($back.P1) { New-Result 'PASS' } else { New-Result 'FAIL' "node cannot reach ${Controller}:$ControllerGrpcPort" }
            $c.C5200 = if ($back.P2) { New-Result 'PASS' } else { New-Result 'FAIL' "node cannot reach ${Controller}:$ControllerHttpPort" }
        } catch {
            $c.C5100 = New-Result 'SKIP' 'remote probe failed'
            $c.C5200 = New-Result 'SKIP' 'remote probe failed'
        }
    } else {
        $c.C5100 = New-Result 'SKIP' 'needs WinRM'
        $c.C5200 = New-Result 'SKIP' 'needs WinRM'
    }

    # Firewall: Allow rules present, and no Block rule naming the agent executable.
    try {
        $cs = New-CimSession -ComputerName $node -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 20 -ErrorAction Stop
        try {
            $all    = @(Get-NetFirewallRule -CimSession $cs -ErrorAction Stop)
            $allow  = @($all | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' -and $_.DisplayName -match 'TestAgent' })
            $blocks = New-Object System.Collections.Generic.List[string]
            foreach ($r in @($all | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Block' })) {
                $app = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -CimSession $cs -ErrorAction SilentlyContinue
                if ($app -and $app.Program -and $app.Program -match 'testagentgrpc\.exe') { [void]$blocks.Add($r.DisplayName) }
            }
            if ($blocks.Count)      { $c.FW = New-Result 'FAIL' ("BLOCK: " + ($blocks -join ', ')) }
            elseif (-not $allow.Count) { $c.FW = New-Result 'WARN' 'no TestAgent Allow rules' }
            else                    { $c.FW = New-Result 'PASS' ("{0} allow, 0 block" -f $allow.Count) }
        } finally { Remove-CimSession $cs -ErrorAction SilentlyContinue }
    } catch { $c.FW = New-Result 'SKIP' 'firewall query unavailable' }

    # .NET shared frameworks, read over SMB (works with WinRM down).
    if ($smbOk) {
        $asp  = Get-SharedFxVersion -Node $node -Framework 'Microsoft.AspNetCore.App'
        $desk = Get-SharedFxVersion -Node $node -Framework 'Microsoft.WindowsDesktop.App'
        $parts = @()
        if ($asp)  { $parts += "asp=$asp" }   else { $parts += 'asp=none' }
        if ($desk) { $parts += "desk=$desk" } else { $parts += 'desk=none' }
        $ok = ($asp -and $asp -ge $MinDotNet -and $desk -and $desk -ge $MinDotNet)
        $c.NET = if ($ok) { New-Result 'PASS' ($parts -join ' ') } else { New-Result 'FAIL' (($parts -join ' ') + " (need $MinDotNet)") }
    } else { $c.NET = New-Result 'SKIP' 'needs SMB' }

    if ($registry.Ok) {
        $hit = $registry.Agents | Where-Object { $_ -and $_.ToString().ToUpper() -eq $node.ToUpper() }
        $c.REG = if ($hit) { New-Result 'PASS' 'registered' } else { New-Result 'FAIL' 'not in controller registry' }
    } else { $c.REG = New-Result 'SKIP' $registry.Note }

    [pscustomobject]@{ Node = $node; Checks = $c }
}

# ---------------------------------------------------------------------------
#  Report
# ---------------------------------------------------------------------------
$columns = @('PING','DNS','SMB','APORT','WINRM','C5100','C5200','FW','NET','REG')

Write-Host ''
Write-Host ('  {0,-10} {1}' -f 'NODE', (($columns | ForEach-Object { '{0,-6}' -f $_ }) -join '')) -ForegroundColor White
Write-Host ('  ' + ('-' * (10 + 1 + ($columns.Count * 6)))) -ForegroundColor DarkGray

foreach ($r in $rows) {
    Write-Host ('  {0,-10} ' -f $r.Node) -NoNewline
    foreach ($col in $columns) {
        $state = $r.Checks[$col].State
        $colour = switch ($state) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } default { 'DarkGray' } }
        Write-Host ('{0,-6}' -f $state) -NoNewline -ForegroundColor $colour
    }
    Write-Host ''
}

$failed = foreach ($r in $rows) {
    foreach ($col in $columns) {
        if ($r.Checks[$col].State -in @('FAIL','WARN')) {
            [pscustomobject]@{ Node = $r.Node; Check = $col; State = $r.Checks[$col].State; Detail = $r.Checks[$col].Detail }
        }
    }
}

Write-Host ''
if ($failed) {
    Write-Host '  DETAIL (FAIL / WARN only)' -ForegroundColor Yellow
    foreach ($f in $failed) {
        Write-Host ('    {0,-10} {1,-6} {2,-5} {3}' -f $f.Node, $f.Check, $f.State, $f.Detail) -ForegroundColor $(if ($f.State -eq 'FAIL') { 'Red' } else { 'Yellow' })
    }
} else {
    Write-Host '  All checks passed.' -ForegroundColor Green
}

$failCount = @($failed | Where-Object { $_.State -eq 'FAIL' }).Count
Write-Host ''
# @() around $rows: a single node yields one object, which has no .Count in PS 5.1.
Write-Host ('  Nodes: {0}   FAIL: {1}   WARN: {2}' -f @($rows).Count, $failCount, @($failed | Where-Object { $_.State -eq 'WARN' }).Count) -ForegroundColor White

if ($ReportPath) {
    $flat = foreach ($r in $rows) {
        $o = [ordered]@{ Node = $r.Node }
        foreach ($col in $columns) { $o[$col] = $r.Checks[$col].State; $o["${col}_detail"] = $r.Checks[$col].Detail }
        [pscustomobject]$o
    }
    $flat | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host ("  Report: {0}" -f $ReportPath) -ForegroundColor DarkGray
}

if ($failCount -gt 0) { exit 1 } else { exit 0 }
