<#
.SYNOPSIS
  Configures Windows Service Manager recovery for TestAgentService so it
  restarts after the self-healing watchdog exits the process.

.DESCRIPTION
  Part 3 of the self-healing gRPC agent system (see
  docs/Issues/SelfHealing_Grpc_Agent_Spec.md).

  - Sets escalating restart delays (5s, 10s, 30s) to avoid tight crash loops.
  - Sets failureflag=1 so NON-ZERO exit codes (the watchdog's Environment.ExitCode=3)
    count as failures, not just hard crashes. This is the key setting: without it
    Windows treats a clean exit(3) as a normal stop and does NOT restart.
  - Resets the failure counter after 60s of healthy running.

.PARAMETER ServiceName
  The Windows service name. Defaults to "TestAgentGrpc".

.NOTES
  Run as Administrator. Run on every agent (bake into provisioning / VM-revert
  scripts so the policy survives reverts).

.EXAMPLE
  .\Configure-AgentRecovery.ps1 -ServiceName TestAgentGrpc
#>

param(
    [string]$ServiceName = "TestAgentGrpc"
)

$ErrorActionPreference = "Stop"

Write-Host "Configuring recovery policy for $ServiceName ..."

# Verify the service exists
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "Service '$ServiceName' not found. Install the agent service first."
}

# Set failure actions: reset counter after 60s; restart with escalating delays.
# Delays are in milliseconds: 5s, 10s, 30s.
& sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure returned $LASTEXITCODE" }

# CRITICAL: treat non-zero exit codes as failures (catches the watchdog's exit 3).
# Without this, a clean Environment.Exit(3) would NOT trigger a restart.
& sc.exe failureflag $ServiceName 1
if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag returned $LASTEXITCODE" }

Write-Host "Recovery policy configured:"
Write-Host "  - Restart after 5s / 10s / 30s on successive failures"
Write-Host "  - Non-zero exit codes treated as failures (failureflag=1)"
Write-Host "  - Failure counter resets after 60s healthy"

# Show the resulting config for verification
Write-Host "`nCurrent configuration:"
& sc.exe qfailure $ServiceName
