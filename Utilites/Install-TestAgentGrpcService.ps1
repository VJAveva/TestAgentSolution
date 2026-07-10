#Requires -Version 5.1
<#
.SYNOPSIS
    Installs and configures the TestAgent gRPC Windows Service,
    running it under a domain account you supply as parameters.

.PARAMETER ServiceUser
    The account the service runs as, in DOMAIN\User form.
    Example: Magellandev2000\wwuApps   (required)

.PARAMETER ServicePassword
    The account's password. If omitted, the script prompts for it
    securely (masked) instead of taking it on the command line.

.EXAMPLE
    .\Install-TestAgentGrpcService.ps1 -ServiceUser "Magellandev2000\wwuApps" -ServicePassword "P@ssw0rd"

.EXAMPLE
    # Omit the password to be prompted for it (kept out of history):
    .\Install-TestAgentGrpcService.ps1 -ServiceUser "Magellandev2000\wwuApps"
#>

param(
    [Parameter(Mandatory)]
    [string]$ServiceUser,

    [string]$ServicePassword
)

# ----------------------- Configuration ------------------------------
$ServiceName = 'TestAgentGrpc'
$DisplayName = 'TestAgent gRPC Service'
$BinaryPath  = 'C:\TestAgentService\TestAgentGrpc.exe'
# --------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# ---- 1. Ensure we are running as Administrator ---------------------
#   If not elevated, relaunch elevated and forward the same parameters.
$isAdmin = ([Security.Principal.WindowsPrincipal] `
            [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Not running as Administrator - relaunching with elevation..." -ForegroundColor Yellow
    $fwd = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ServiceUser '$ServiceUser'"
    if ($ServicePassword) { $fwd += " -ServicePassword '$ServicePassword'" }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $fwd -Verb RunAs
    exit
}

# ---- 2. Validate the executable exists -----------------------------
if (-not (Test-Path -LiteralPath $BinaryPath)) {
    Write-Host "ERROR: Executable not found at '$BinaryPath'" -ForegroundColor Red
    exit 1
}

# ---- 3. Build the credential from the supplied parameters ----------
if ($ServicePassword) {
    $securePass = ConvertTo-SecureString $ServicePassword -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential($ServiceUser, $securePass)
}
else {
    # No password parameter supplied - prompt for it (masked).
    $cred = Get-Credential -UserName $ServiceUser -Message "Password for service account $ServiceUser"
}

if (-not $cred) {
    Write-Host "No credentials supplied - aborting (nothing was changed)." -ForegroundColor Red
    exit 1
}

# ---- 4. Remove existing service (makes the script re-runnable) -----
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists - removing it first..." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2   # let the Service Control Manager release the name
}

# ---- 5. Create the service running as the supplied account ---------
Write-Host "Creating service '$ServiceName' (running as $ServiceUser)..." -ForegroundColor Cyan
New-Service -Name $ServiceName `
            -DisplayName $DisplayName `
            -BinaryPathName "`"$BinaryPath`"" `
            -StartupType Automatic `
            -Credential $cred

# ---- 6. Switch to Automatic (Delayed Start) ------------------------
sc.exe config $ServiceName start= delayed-auto
if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit code $LASTEXITCODE)" }

# ---- 7. Configure crash recovery -----------------------------------
#   Restart after 5s, then 10s, then 30s. Failure counter resets after 60s healthy.
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed (exit code $LASTEXITCODE)" }

# ---- 8. Also recover when the service stops with an error code ------
sc.exe failureflag $ServiceName 1
if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed (exit code $LASTEXITCODE)" }

# ---- 9. Start the service ------------------------------------------
Write-Host "Starting service..." -ForegroundColor Cyan
try {
    Start-Service -Name $ServiceName
}
catch {
    Write-Host ""
    Write-Host "The service was created but failed to start." -ForegroundColor Red
    Write-Host "Most likely cause: the account '$ServiceUser' does not have the" -ForegroundColor Yellow
    Write-Host "'Log on as a service' right, or the password is incorrect." -ForegroundColor Yellow
    Write-Host "Grant the right here:  secpol.msc -> Local Policies ->" -ForegroundColor Yellow
    Write-Host "User Rights Assignment -> 'Log on as a service' -> add $ServiceUser." -ForegroundColor Yellow
    Write-Host "(For a domain account this is often set via Group Policy.)" -ForegroundColor Yellow
    throw
}

# ---- Done ----------------------------------------------------------
$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "Done. '$ServiceName' is running as $ServiceUser and is now '$($svc.Status)'." -ForegroundColor Green
