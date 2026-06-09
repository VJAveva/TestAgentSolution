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