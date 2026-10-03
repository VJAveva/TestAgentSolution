# Save as: C:\TestAgentService\Diagnose-AgentPort.ps1
param([int]$Port = 5200)

Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
Write-Host " TestAgent Port $Port Diagnostic" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan

Write-Host "`nCheck 1: Is the TestAgent service running?"
$service = Get-Service TestAgentService -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "  Service status: $($service.Status)" -ForegroundColor $(
        if ($service.Status -eq 'Running') { 'Green' } else { 'Red' })
} else {
    Write-Host "  Service NOT installed!" -ForegroundColor Red
}

Write-Host "`nCheck 2: TestAgentGrpc processes running"
$procs = Get-Process TestAgentGrpc -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Format-Table Id, ProcessName, StartTime, Path -AutoSize
} else {
    Write-Host "  No TestAgentGrpc process found" -ForegroundColor Yellow
}

Write-Host "`nCheck 3: What's listening on port $Port?"
$listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listeners) {
    $listeners | ForEach-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        [PSCustomObject]@{
            LocalAddress = $_.LocalAddress
            Port = $_.LocalPort
            PID = $_.OwningProcess
            Process = $p.Name
            Path = $p.Path
        }
    } | Format-Table -AutoSize
} else {
    Write-Host "  Nothing is listening on port $Port" -ForegroundColor Red
}

Write-Host "`nCheck 4: Connections in TIME_WAIT on port $Port"
$timeWait = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
    Where-Object State -ne Listen
if ($timeWait) {
    Write-Host "  Found $($timeWait.Count) connection(s) in transient states:" -ForegroundColor Yellow
    $timeWait | Format-Table LocalAddress, LocalPort, RemoteAddress, State -AutoSize
} else {
    Write-Host "  No transient connections" -ForegroundColor Green
}

Write-Host "`nCheck 5: Firewall rules for port $Port"
Get-NetFirewallPortFilter | Where-Object LocalPort -eq $Port -ErrorAction SilentlyContinue |
    ForEach-Object {
        $rule = Get-NetFirewallRule -AssociatedNetFirewallPortFilter $_
        [PSCustomObject]@{
            Rule = $rule.DisplayName
            Direction = $rule.Direction
            Action = $rule.Action
            Enabled = $rule.Enabled
        }
    } | Format-Table -AutoSize

Write-Host "`nCheck 6: Last 20 lines of agent log"
$logFile = Get-ChildItem "C:\TestAgentService\Logs\agent-*-$(Get-Date -Format yyyyMMdd).log" `
    -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile) {
    Get-Content $logFile.FullName -Tail 20
} else {
    Write-Host "  No log file found for today" -ForegroundColor Yellow
}