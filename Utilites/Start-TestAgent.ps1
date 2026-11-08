<#
.SYNOPSIS
    Starts TestAgentGrpc cleanly: kills a "zombie" process that runs but does not listen
    on 5200, starts the exe from C:\TestAgentService and waits up to 15 s for the port.

.DESCRIPTION
    Takes no parameters (path, port and wait are fixed at the top of the script).
    Exit codes: 0 = listening, 1 = did not start listening in time.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\TestAgentService\Start-TestAgent.ps1
#>

$exeName = "TestAgentGrpc"
$exePath = "C:\TestAgentService\TestAgentGrpc.exe"
$port = 5200
$maxWaitSeconds = 15

# Check if already running and healthy
$process = Get-Process -Name $exeName -ErrorAction SilentlyContinue
if ($process) {
    $listening = netstat -ano | Select-String ":$port\s+.*LISTENING"
    if ($listening) {
        Write-Host "[INFO] $exeName is already running and listening on port $port. Nothing to do."
        exit 0
    }
    else {
        Write-Host "[WARN] Zombie process detected (PID: $($process.Id)). Killing..."
        Stop-Process -Name $exeName -Force
        Start-Sleep -Seconds 3
    }
}

# Launch with correct working directory
Write-Host "[STATUS] Starting $exeName..."
Set-Location -Path "C:\TestAgentService"
Start-Process -FilePath $exePath -WorkingDirectory "C:\TestAgentService"

# Wait and verify port binding
$elapsed = 0
while ($elapsed -lt $maxWaitSeconds) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    $listening = netstat -ano | Select-String ":$port\s+.*LISTENING"
    if ($listening) {
        Write-Host "[SUCCESS] $exeName started and listening on port $port."
        exit 0
    }
}

Write-Host "[ERROR] $exeName started but not listening on port $port after $maxWaitSeconds seconds."
exit 1