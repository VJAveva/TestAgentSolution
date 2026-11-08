<#
.SYNOPSIS
    LEGACY: registers an at-logon scheduled task "TestAgentGrpc" that runs
    C:\TestAgentService\Start-TestAgent.ps1 as user wwAPPS.

.DESCRIPTION
    Takes no parameters. Run elevated, on the agent, logged on as wwAPPS.
    Superseded by TestAgent_SetupScripts\Prepare-AgentNode-Full.ps1, which creates the
    task the fleet tooling expects ("TestAgentGrpc Interactive") with the right logon
    type and rights. Do not run both: you would get two tasks starting the agent.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\AgentSchedular.ps1
#>
$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File C:\TestAgentService\Start-TestAgent.ps1" `
    -WorkingDirectory "C:\TestAgentService"

$trigger = New-ScheduledTaskTrigger -AtLogOn

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName "TestAgentGrpc" `
    -Action $action -Trigger $trigger -Settings $settings `
    -User "wwAPPS" -RunLevel Highest -Force