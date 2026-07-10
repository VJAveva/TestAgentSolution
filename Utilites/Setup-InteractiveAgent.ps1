#Requires -Version 5.1
<#
.SYNOPSIS
    Prepares an agent node to run UI / Coded UI tests by running TestAgentGrpc
    in an interactive desktop session instead of as a Session 0 service.

.DESCRIPTION
    1. Self-elevates if needed (forwards the same parameters).
    2. Verifies the agent executable exists.
    3. Validates that the logon account actually resolves to a SID (fails early
         with guidance if the name is misspelled or not found).
    4. Configures auto-logon for the test account (Autologon.exe if present;
         registry fallback with a plaintext warning).
    5. Registers a logon-triggered Scheduled Task that starts TestAgentGrpc
         INTERACTIVE + HIGHEST PRIVILEGES  -> runs in the real desktop session.
    6. Disables screen lock, screensaver, sleep, and display timeout.

    BEST PRACTICE: log on as the test account itself, open an elevated PowerShell,
    and run this script there. Then every setting lands on the right account in one pass.

.PARAMETER LogonUser
    The auto-logon test account in DOMAIN\User form (or .\User for a local account).

.PARAMETER LogonPassword
    The account password. If omitted, you are prompted for it (masked).

.PARAMETER AgentExePath
    Full path to TestAgentGrpc.exe. Default: C:\TestAgentService\TestAgentGrpc.exe

.PARAMETER AutologonPath
    Optional path to Sysinternals Autologon.exe.
    Download: https://learn.microsoft.com/sysinternals/downloads/autologon

.EXAMPLE
    .\Setup-InteractiveAgent.ps1 -LogonUser "Magellandev2000\wwApps" -LogonPassword "P@ssw0rd"
#>

param(
    [Parameter(Mandatory)]
    [string]$LogonUser,

    [string]$LogonPassword,

    [string]$AgentExePath = 'C:\TestAgentService\TestAgentGrpc.exe',

    [string]$AutologonPath,

    [string]$TaskName = 'TestAgentGrpc Interactive'
)

$ErrorActionPreference = 'Stop'

# ---- 1. Ensure we are running as Administrator ---------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
            [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Not running as Administrator - relaunching with elevation..." -ForegroundColor Yellow
    $fwd = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`" -LogonUser '$LogonUser' -AgentExePath '$AgentExePath'"
    if ($LogonPassword)  { $fwd += " -LogonPassword '$LogonPassword'" }
    if ($AutologonPath)  { $fwd += " -AutologonPath '$AutologonPath'" }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $fwd -Verb RunAs
    exit
}

# ---- 2. Validate the agent executable ------------------------------
if (-not (Test-Path -LiteralPath $AgentExePath)) {
    Write-Host "ERROR: Agent executable not found at '$AgentExePath'" -ForegroundColor Red
    exit 1
}

# ---- 3. Validate that the logon account resolves to a SID ----------
#   This is the exact check Register-ScheduledTask does internally. Doing it
#   here turns the cryptic 0x80070534 into a clear, actionable message.
$sid = $null
try {
    $sid = ([System.Security.Principal.NTAccount]$LogonUser).Translate(
               [System.Security.Principal.SecurityIdentifier])
} catch { }

if (-not $sid) {
    $namePart   = $LogonUser.Split('\')[0]
    Write-Host "ERROR: Windows cannot resolve the account '$LogonUser' to a security ID." -ForegroundColor Red
    Write-Host "       The name is almost certainly misspelled, or it is a LOCAL account on a" -ForegroundColor Yellow
    Write-Host "       machine whose name is not '$namePart'." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "This machine:" -ForegroundColor Cyan
    Write-Host "   Computer name : $env:COMPUTERNAME"
    try {
        $cs = Get-CimInstance Win32_ComputerSystem
        Write-Host "   Domain        : $($cs.Domain)   (PartOfDomain: $($cs.PartOfDomain))"
    } catch { }
    Write-Host ""
    Write-Host "Enabled local accounts on this box:" -ForegroundColor Cyan
    try {
        Get-LocalUser | Where-Object Enabled |
            Select-Object -ExpandProperty Name | ForEach-Object { Write-Host "   $_" }
    } catch { Write-Host "   (could not list local users)" }
    Write-Host ""
    Write-Host "Fix the name and re-run. For a LOCAL account you can also use:" -ForegroundColor Yellow
    Write-Host "   -LogonUser `".\<username>`"   or   -LogonUser `"$env:COMPUTERNAME\<username>`"" -ForegroundColor Yellow
    exit 1
}
Write-Host "Account '$LogonUser' resolved OK (SID $($sid.Value))." -ForegroundColor Green

# ---- 4. Resolve user / domain / password ---------------------------
if ($LogonUser -match '\\') {
    $domain, $user = $LogonUser -split '\\', 2
} else {
    $domain = '.'            # local account
    $user   = $LogonUser
}

if ($LogonPassword) {
    $plainPassword = $LogonPassword
} else {
    $cred = Get-Credential -UserName $LogonUser -Message "Password for auto-logon account $LogonUser"
    if (-not $cred) {
        Write-Host "No password supplied - aborting (nothing was changed)." -ForegroundColor Red
        exit 1
    }
    $plainPassword = $cred.GetNetworkCredential().Password
}

# ---- 5. Configure auto-logon ---------------------------------------
# Prefer Sysinternals Autologon.exe (stores password as an encrypted LSA secret).
$autologonExe = $null
foreach ($candidate in @(
        $AutologonPath,
        (Join-Path $PSScriptRoot 'Autologon.exe'),
        (Join-Path $PSScriptRoot 'Autologon64.exe')
    )) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) { $autologonExe = $candidate; break }
}
if (-not $autologonExe) {
    $found = Get-Command 'Autologon.exe', 'Autologon64.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $autologonExe = $found.Source }
}

if ($autologonExe) {
    Write-Host "Configuring auto-logon via Autologon.exe (encrypted LSA secret)..." -ForegroundColor Cyan
    & $autologonExe -accepteula $user $domain $plainPassword | Out-Null
} else {
    Write-Host "Autologon.exe not found - using the registry method instead." -ForegroundColor Yellow
    Write-Host "WARNING: the registry method stores the password in PLAINTEXT under Winlogon." -ForegroundColor Yellow
    Write-Host "         For better security, download Autologon.exe and re-run with -AutologonPath." -ForegroundColor Yellow
    $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-ItemProperty -Path $winlogon -Name 'AutoAdminLogon'    -Value '1'            -Type String
    Set-ItemProperty -Path $winlogon -Name 'DefaultUserName'   -Value $user          -Type String
    Set-ItemProperty -Path $winlogon -Name 'DefaultDomainName' -Value $domain        -Type String
    Set-ItemProperty -Path $winlogon -Name 'DefaultPassword'   -Value $plainPassword -Type String
    Remove-ItemProperty -Path $winlogon -Name 'AutoLogonCount' -ErrorAction SilentlyContinue
}

# ---- 6. Register the logon-triggered Scheduled Task ----------------
#   LogonType Interactive  => runs ONLY in the live desktop session (not Session 0)
#   RunLevel  Highest      => runs elevated, no UAC prompt
Write-Host "Registering scheduled task '$TaskName'..." -ForegroundColor Cyan
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

$action    = New-ScheduledTaskAction -Execute $AgentExePath -WorkingDirectory (Split-Path -Parent $AgentExePath)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $LogonUser
$principal = New-ScheduledTaskPrincipal -UserId $LogonUser -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
                -ExecutionTimeLimit ([TimeSpan]::Zero)

try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
                           -Principal $principal -Settings $settings -ErrorAction Stop | Out-Null
} catch {
    Write-Host "ERROR: failed to register the scheduled task." -ForegroundColor Red
    Write-Host "       $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ---- 7. Keep the session awake and unlocked ------------------------
Write-Host "Disabling sleep, display timeout, screensaver, and auto-lock..." -ForegroundColor Cyan

# Power: never turn off display / sleep / hibernate / spin down disk (AC + DC)
foreach ($s in 'monitor-timeout-ac','monitor-timeout-dc','standby-timeout-ac','standby-timeout-dc',
                'hibernate-timeout-ac','hibernate-timeout-dc','disk-timeout-ac','disk-timeout-dc') {
    powercfg /change $s 0 | Out-Null
}

# Machine: never auto-lock on inactivity
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
                 -Name 'InactivityTimeoutSecs' -Value 0 -Type DWord

# Current user: disable the screensaver and its password lock
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveActive'    -Value '0' -Type String
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaverIsSecure' -Value '0' -Type String
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ScreenSaveTimeOut'   -Value '0' -Type String

# ---- 8. Summary ----------------------------------------------------
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Auto-logon account : $LogonUser" -ForegroundColor Green
Write-Host "  Startup task       : $TaskName  (state: $($task.State))" -ForegroundColor Green
Write-Host "  Agent              : $AgentExePath  (interactive, elevated)" -ForegroundColor Green
Write-Host ""
Write-Host "Reboot the node to test the full cycle: auto-logon -> task fires -> agent runs on the desktop." -ForegroundColor Cyan
Write-Host "On Hyper-V, keep the VMConnect CONSOLE session logged on. Do NOT use RDP - disconnecting RDP locks the session and breaks UI automation." -ForegroundColor Yellow
