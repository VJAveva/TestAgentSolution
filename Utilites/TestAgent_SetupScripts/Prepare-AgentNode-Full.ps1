#Requires -Version 5.1
<#
.SYNOPSIS
    TestAgent AGENT node - one-click, self-healing prep for a fresh / reverted VM
    so TestAgentGrpc runs automatically on the real desktop. Sibling of
    Prepare-ControllerNode.ps1 (same conventions, same secure credential handling).

.DESCRIPTION
    Run ONCE on an agent machine. Safe to re-run (idempotent): every phase checks
    what is already in place and only fills the gaps. The script self-elevates.

    It ASKS for the run-as credentials (username + password) with a masked
    Get-Credential prompt, and uses them ONLY to:
      - register the "TestAgentGrpc Interactive" scheduled task to run as that user, and
      - (optional) set auto-logon via Sysinternals Autologon.exe (encrypted LSA secret).
    The password is never placed on a command line, in the log/transcript, or in the
    registry as plain text.

    Phases:
      [1] Folders ................................ C:\TestAgentService, TestSetup, TestBinaries, TestResults
      [2] .NET 10 (ASP.NET Core + Desktop + Core)  Ensure-DotNetRuntime  (Kestrel/gRPC + tray UI)
      [3] Firewall allow rules ................... agent 5200 in, controller 5100 out, exe, ping (targeted, not a blanket off)
      [4] appsettings.json ...................... controller address + agent port
      [5] Run-as account ........................ local admin + service/batch/interactive logon rights
      [6] Disable UAC (registry) ................ -SkipUacDisable to keep it
      [7] Trust URLs + suppress Open-File prompts  -SkipSecurityPrompts to keep them
      [8] Credentials + scheduled task .......... "TestAgentGrpc Interactive" (AtLogon/Interactive/Highest) + optional auto-logon
      [9] Start the agent + verify port 5200

.NOTES
    LAB / TEST posture only (reverted automation VMs). Do NOT apply to a shared or
    production machine. Mirrors the floor in Prepare-ControllerNode.ps1 (.NET 10.0.12).
    Version 1.0
#>

param(
    # --- Controller / ports ---
    [string]   $ControllerAddress   = 'http://jvgr22:5100',       # agent -> controller (register / heartbeat / events)
    [int]      $ControllerPort       = 5100,
    [int]      $AgentPort            = 5200,                       # controller -> this agent (gRPC command dispatch)

    # --- Paths ---
    [string]   $InstallDir           = 'C:\TestAgentService',     # where TestAgentGrpc.exe + appsettings.json live
    [string]   $AgentExeName         = 'TestAgentGrpc.exe',
    [string]   $TaskName             = 'TestAgentGrpc Interactive',

    # --- Run-as account (asked interactively unless -LogonCredential is passed) ---
    [string]   $LogonUser            = '',                        # e.g. magellandev2000\wwApps ; blank => prompted
    [pscredential] $LogonCredential,                             # pass to run unattended; else Get-Credential prompts (masked)
    [switch]   $NoPrompt,                                         # do not prompt; skip task/auto-logon if no credential
    [switch]   $EnableAutoLogon,                                  # also set auto-logon (prefers Autologon.exe = LSA secret)
    [string]   $AutologonPath,                                    # path to Sysinternals Autologon.exe (optional)

    # --- .NET 10 ---
    [string]   $DotNetInstallDir     = 'C:\Program Files\dotnet',
    [ValidateSet('x64','x86')]
    [string[]] $DotNetArchitectures  = @('x64','x86'),
    [switch]   $SkipNetCore,
    [string]   $ReleaseMetadataUrl,
    [string]   $OfflineInstallerDir,

    # --- Zone-relaxation trusted hosts ---
    [string[]] $ExtraTrustedHosts    = @(),

    # --- Logging ---
    [string]   $LogFile              = 'C:\TestAgentService\Logs\Prepare-AgentNode.log',

    # --- Phase skips / modes ---
    [switch]   $SkipDotNet,
    [switch]   $SkipFirewall,
    [switch]   $SkipRunAsAccount,
    [switch]   $SkipUacDisable,
    [switch]   $SkipSecurityPrompts,
    [switch]   $SkipStart,
    [switch]   $Uninstall,
    [switch]   $RebootWhenDone,
    [switch]   $NoElevate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$script:DotNetMajor      = 10
$script:DotNetMinVersion = [version]'10.0.12'                     # must match Prepare-ControllerNode.ps1
$AgentExePath            = Join-Path $InstallDir $AgentExeName
$IncludeNetCore          = -not $SkipNetCore
$script:Warnings         = New-Object System.Collections.Generic.List[string]
$script:RebootNeeded     = $false
$script:Phase            = 0
$script:TotalPhases      = 9

# Derive controller host for firewall / zone rules.
$script:ControllerHost = $ControllerAddress
try { $script:ControllerHost = ([Uri]$ControllerAddress).Host } catch { }

# ----------------------------------------------------------------------------
#  Console helpers (ASCII-only)
# ----------------------------------------------------------------------------
function Write-Ok    ($m) { Write-Host "  [ OK ] $m" -ForegroundColor Green  }
function Write-Warn  ($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Write-Err   ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red    }
function Write-Note  ($m) { Write-Host "  $m"        -ForegroundColor Gray   }
function Add-Warn    ($m) { $script:Warnings.Add($m) | Out-Null; Write-Warn $m }
function Write-Phase ($m) { $script:Phase++; Write-Host ''; Write-Host ("[{0}/{1}] {2}" -f $script:Phase, $script:TotalPhases, $m) -ForegroundColor Cyan }

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-RegValue {
    param([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord')
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

function Get-PlainFromSecure {
    # Returns the password as a plain string ONLY at the point of use; never caches or logs it.
    param([System.Security.SecureString]$Secure)
    if (-not $Secure) { return $null }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try   { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ----------------------------------------------------------------------------
#  Self-elevate (forward NON-SECRET params only; the elevated instance prompts
#  for the credential so the password never lands on a command line)
# ----------------------------------------------------------------------------
if (-not (Test-Admin)) {
    if ($NoElevate)         { Write-Err 'Not elevated and -NoElevate was specified. Re-run as Administrator.'; exit 1 }
    if (-not $PSCommandPath) { Write-Err 'Cannot self-elevate (no script path). Run from an elevated PowerShell prompt.'; exit 1 }

    Write-Host 'Not running as Administrator - relaunching elevated...' -ForegroundColor Yellow
    $fwd = @(
        '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File', "`"$PSCommandPath`"",
        '-ControllerAddress', "`"$ControllerAddress`"",
        '-ControllerPort',     $ControllerPort,
        '-AgentPort',          $AgentPort,
        '-InstallDir',        "`"$InstallDir`"",
        '-AgentExeName',      "`"$AgentExeName`"",
        '-TaskName',          "`"$TaskName`"",
        '-DotNetInstallDir',  "`"$DotNetInstallDir`"",
        '-DotNetArchitectures', ($DotNetArchitectures -join ','),
        '-LogFile',           "`"$LogFile`""
    )
    if ($LogonUser)               { $fwd += @('-LogonUser', "`"$LogonUser`"") }
    if ($ExtraTrustedHosts.Count) { $fwd += @('-ExtraTrustedHosts', ($ExtraTrustedHosts -join ',')) }
    if ($ReleaseMetadataUrl)      { $fwd += @('-ReleaseMetadataUrl', "`"$ReleaseMetadataUrl`"") }
    if ($OfflineInstallerDir)     { $fwd += @('-OfflineInstallerDir', "`"$OfflineInstallerDir`"") }
    if ($AutologonPath)           { $fwd += @('-AutologonPath', "`"$AutologonPath`"") }
    foreach ($sw in 'NoPrompt','EnableAutoLogon','SkipNetCore','SkipDotNet','SkipFirewall','SkipRunAsAccount','SkipUacDisable','SkipSecurityPrompts','SkipStart','Uninstall','RebootWhenDone') {
        if ($PSBoundParameters[$sw]) { $fwd += "-$sw" }
    }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $fwd
    exit
}

try { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null } catch { }
try { Start-Transcript -Path $LogFile -Append -ErrorAction Stop | Out-Null } catch { }

# ----------------------------------------------------------------------------
#  Uninstall: remove the firewall rules this script adds (files/runtimes left).
# ----------------------------------------------------------------------------
if ($Uninstall) {
    Write-Host ''
    Write-Host 'Removing TestAgent firewall rules...' -ForegroundColor Yellow
    $removed = 0
    Get-NetFirewallRule -DisplayName 'TestAgent*' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-NetFirewallRule -Name $_.Name -ErrorAction SilentlyContinue
        Write-Host ("  Removed: {0}" -f $_.DisplayName) -ForegroundColor Green; $removed++
    }
    if ($removed -eq 0) { Write-Host '  No TestAgent firewall rules found.' -ForegroundColor Gray }
    Write-Host ("  Scheduled task '{0}' left in place; remove with: Unregister-ScheduledTask -TaskName '{0}'" -f $TaskName) -ForegroundColor Gray
    try { Stop-Transcript | Out-Null } catch { }
    exit 0
}

# ============================================================================
#  .NET runtime engine (official MSI redistributables; per-architecture)
# ============================================================================
function Test-SharedFx {
    param([string]$DotnetBase, [string]$Framework, [version]$Minimum = $script:DotNetMinVersion)
    $dir = Join-Path $DotnetBase "shared\$Framework"
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    $newest = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $v = $null; if ([version]::TryParse(($_.Name -replace '-.*$',''), [ref]$v)) { $v }
    }) | Sort-Object -Descending | Select-Object -First 1
    return [bool]($newest -and $newest -ge $Minimum)
}

function Get-DotNetReleaseInfo {
    param([string[]]$Urls)
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    foreach ($u in $Urls) {
        try   { return (Invoke-RestMethod -UseBasicParsing -Uri $u -ErrorAction Stop) }
        catch { Write-Note "Release metadata unavailable from $u" }
    }
    $null
}

function Get-FileFromRelease {
    param($FileList, [string]$Rid, [switch]$CoreRuntime)
    if ($CoreRuntime) {
        $FileList | Where-Object { $_.rid -eq $Rid -and $_.name -like 'dotnet-runtime-*-win-*.exe' } | Select-Object -First 1
    } else {
        $FileList | Where-Object { $_.rid -eq $Rid -and $_.name -like '*.exe' -and $_.name -notlike 'dotnet-hosting-*' } | Select-Object -First 1
    }
}

function Install-RuntimeExe {
    param([string]$Label, $FileEntry)
    if (-not $FileEntry -or -not $FileEntry.url) { Add-Warn "$Label : no matching installer in the release feed."; return }
    $fileName = Split-Path -Leaf ([Uri]$FileEntry.url).AbsolutePath
    $local = $null
    if ($OfflineInstallerDir) {
        $staged = Join-Path $OfflineInstallerDir $fileName
        if (Test-Path -LiteralPath $staged) { $local = $staged; Write-Note "Using staged installer: $fileName" }
    }
    if (-not $local) {
        $local = Join-Path $env:TEMP $fileName
        Write-Note "Downloading $Label ($fileName)..."
        try { Invoke-WebRequest -UseBasicParsing -Uri $FileEntry.url -OutFile $local -ErrorAction Stop }
        catch { Add-Warn "$Label : download failed ($($_.Exception.Message))."; return }
    }
    if ($FileEntry.hash) {
        $actual = (Get-FileHash -Algorithm SHA512 -LiteralPath $local).Hash
        if ($actual -ne ([string]$FileEntry.hash).ToUpper()) { Add-Warn "$Label : SHA512 mismatch on $fileName - refusing to run it."; return }
    }
    $log = Join-Path (Split-Path -Parent $LogFile) ("dotnet-" + [IO.Path]::GetFileNameWithoutExtension($fileName) + ".log")
    try {
        $proc = Start-Process -FilePath $local -ArgumentList @('/install','/quiet','/norestart','/log',"`"$log`"") -Wait -PassThru -ErrorAction Stop
        switch ($proc.ExitCode) {
            0    { Write-Note "$Label installed (exit 0)" }
            3010 { Write-Note "$Label installed (reboot pending)"; $script:RebootNeeded = $true }
            1641 { Write-Note "$Label installed (reboot)";         $script:RebootNeeded = $true }
            1638 { Write-Note "$Label : equal/newer already present - coexisting" }
            default { Write-Note "$Label installer exit $($proc.ExitCode) - verifying on disk" }
        }
    } catch { Add-Warn "$Label : installer failed to launch ($($_.Exception.Message))." }
}

# ============================================================================
#  PHASE 1 - folders
# ============================================================================
function Ensure-Folders {
    foreach ($d in $InstallDir,'C:\TestSetup','C:\TestBinaries','C:\TestResults',(Join-Path $InstallDir 'Logs')) {
        if (Test-Path -LiteralPath $d) { Write-Note "Dir exists: $d" }
        else { New-Item -ItemType Directory -Force -Path $d | Out-Null; Write-Ok "Created dir: $d" }
    }
}

# ============================================================================
#  PHASE 2 - .NET 10 (ASP.NET Core runtime for Kestrel/gRPC + Windows Desktop + Core)
# ============================================================================
function Ensure-DotNetRuntime {
    $major   = $script:DotNetMajor
    $channel = "$major.0"
    $baseFor = @{ 'x64' = $DotNetInstallDir; 'x86' = (Join-Path ${env:ProgramFiles(x86)} 'dotnet') }

    $needAsp = @{}; $needDesk = @{}; $needCore = @{}
    foreach ($arch in $DotNetArchitectures) {
        $needAsp[$arch]  = -not (Test-SharedFx $baseFor[$arch] 'Microsoft.AspNetCore.App')
        $needDesk[$arch] = -not (Test-SharedFx $baseFor[$arch] 'Microsoft.WindowsDesktop.App')
        $needCore[$arch] = $IncludeNetCore -and (-not (Test-SharedFx $baseFor[$arch] 'Microsoft.NETCore.App'))
    }
    if (-not (($needAsp.Values -contains $true) -or ($needDesk.Values -contains $true) -or ($needCore.Values -contains $true))) {
        $tail = if ($IncludeNetCore) { ' + .NET Core' } else { '' }
        Write-Ok ".NET $major (ASP.NET Core + Windows Desktop$tail) already present for: $($DotNetArchitectures -join ', ')"
        return
    }
    foreach ($arch in $DotNetArchitectures) {
        $miss = @()
        if ($needAsp[$arch])  { $miss += 'ASP.NET-Core' }
        if ($needDesk[$arch]) { $miss += 'Windows-Desktop' }
        if ($needCore[$arch]) { $miss += '.NET-Core' }
        if ($miss) { Write-Note ("Missing for {0}: {1}" -f $arch, ($miss -join ' + ')) }
    }

    $primary = if ($ReleaseMetadataUrl) { $ReleaseMetadataUrl } else { "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" }
    $legacy  = "https://dotnetcli.azureedge.net/dotnet/release-metadata/$channel/releases.json"
    $meta = Get-DotNetReleaseInfo -Urls @($primary, $legacy)
    if (-not $meta) {
        Add-Warn "Could not reach the .NET release feed. Pre-stage installers in -OfflineInstallerDir, or install the .NET $channel ASP.NET Core runtime + Windows Desktop runtime (x64 AND x86) manually from https://dotnet.microsoft.com/download/dotnet/$channel"
        return
    }
    $rel = $meta.releases | Where-Object { $_.'release-version' -eq $meta.'latest-release' } | Select-Object -First 1
    if (-not $rel) { $rel = $meta.releases | Select-Object -First 1 }
    $aspFiles  = $rel.'aspnetcore-runtime'.files
    $deskFiles = $rel.windowsdesktop.files
    $coreFiles = $rel.runtime.files
    Write-Note "Target build: .NET $($rel.'release-version')"

    # Agent is self-hosted (Kestrel), not IIS, so standalone ASP.NET Core runtime per arch.
    foreach ($arch in @($DotNetArchitectures | Where-Object { $needAsp[$_] })) {
        Install-RuntimeExe -Label "ASP.NET Core Runtime $arch $channel" -FileEntry (Get-FileFromRelease $aspFiles -Rid "win-$arch")
    }
    foreach ($arch in @($DotNetArchitectures | Where-Object { $needDesk[$_] })) {
        Install-RuntimeExe -Label "Windows Desktop Runtime $arch $channel" -FileEntry (Get-FileFromRelease $deskFiles -Rid "win-$arch")
    }
    foreach ($arch in @($DotNetArchitectures | Where-Object { $needCore[$_] })) {
        Install-RuntimeExe -Label ".NET Core Runtime $arch $channel" -FileEntry (Get-FileFromRelease $coreFiles -Rid "win-$arch" -CoreRuntime)
    }

    foreach ($arch in $DotNetArchitectures) {
        if (Test-SharedFx $baseFor[$arch] 'Microsoft.AspNetCore.App' $major)    { Write-Ok "ASP.NET Core $major ($arch) ready" }    elseif ($needAsp[$arch])  { Add-Warn "ASP.NET Core $major ($arch) still missing after install." }
        if (Test-SharedFx $baseFor[$arch] 'Microsoft.WindowsDesktop.App' $major) { Write-Ok "Windows Desktop $major ($arch) ready" } elseif ($needDesk[$arch]) { Add-Warn "Windows Desktop $major ($arch) still missing after install." }
        if ($IncludeNetCore) {
            if (Test-SharedFx $baseFor[$arch] 'Microsoft.NETCore.App' $major)    { Write-Ok ".NET Core $major ($arch) ready" }       elseif ($needCore[$arch]) { Add-Warn ".NET Core $major ($arch) still missing after install." }
        }
    }
}

# ============================================================================
#  PHASE 3 - firewall allow rules (targeted; not a blanket off)
# ============================================================================
function New-FwPortRule {
    param([string]$DisplayName, [string]$Direction, [int]$LocalPort, [int]$RemotePort, [string]$Desc)
    if (Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue) { Write-Note "FW exists: $DisplayName"; return }
    $p = @{ DisplayName = $DisplayName; Description = $Desc; Direction = $Direction; Protocol = 'TCP'; Action = 'Allow'; Enabled = 'True'; Profile = 'Any' }
    if ($LocalPort)  { $p.LocalPort  = $LocalPort }
    if ($RemotePort) { $p.RemotePort = $RemotePort }
    New-NetFirewallRule @p | Out-Null
    Write-Ok "FW created: $DisplayName"
}
function Set-Networking {
    New-FwPortRule -DisplayName "TestAgent Inbound gRPC (TCP $AgentPort)"        -Direction Inbound  -LocalPort  $AgentPort      -Desc 'Controller -> agent: gRPC command dispatch'
    New-FwPortRule -DisplayName "TestAgent to Controller Outbound (TCP $ControllerPort)" -Direction Outbound -RemotePort $ControllerPort -Desc 'Agent -> controller: registration, heartbeat, event push'

    # Program rule for the agent exe (both directions) once it is deployed.
    if (Test-Path $AgentExePath) {
        foreach ($dir in 'Inbound','Outbound') {
            $n = "TestAgent app $dir ($AgentExeName)"
            if (-not (Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName $n -Direction $dir -Program $AgentExePath -Action Allow -Enabled True -Profile Any | Out-Null
                Write-Ok "FW created: $n"
            }
        }
    } else {
        Add-Warn "Agent exe not present yet at $AgentExePath - its program firewall rule will be added on the next run after binaries are deployed."
    }

    # Allow inbound ICMPv4 echo so the controller readiness probe (ping) succeeds.
    $ping = 'TestAgent Allow Ping (ICMPv4)'
    if (-not (Get-NetFirewallRule -DisplayName $ping -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ping -Direction Inbound -Protocol ICMPv4 -IcmpType 8 -Action Allow -Enabled True -Profile Any | Out-Null
        Write-Ok "FW created: $ping"
    }

    # Reserve the HTTP.sys URL ACL so a non-admin agent can bind http://+:5200/ (locale-independent Everyone SID).
    try {
        $everyone = (New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')).Translate([System.Security.Principal.NTAccount]).Value
        & netsh http add urlacl url="http://+:$AgentPort/" user="$everyone" *> $null
        Write-Note "URL ACL ensured for http://+:$AgentPort/"
    } catch { Write-Note "URL ACL step skipped ($($_.Exception.Message))" }
}

# ============================================================================
#  PHASE 4 - appsettings.json (controller address + agent port)
# ============================================================================
function Ensure-AgentConfig {
    $appSettings = Join-Path $InstallDir 'appsettings.json'
    $needWrite = $true
    if (Test-Path $appSettings) {
        try {
            $cfg = Get-Content -Raw -LiteralPath $appSettings | ConvertFrom-Json
            if ($cfg.ControllerAddress -and $cfg.AgentGrpcPort) { $needWrite = $false }
        } catch {
            Copy-Item -LiteralPath $appSettings -Destination "$appSettings.bak" -Force
            Add-Warn "Existing appsettings.json was invalid - backed up to appsettings.json.bak and regenerating."
        }
    }
    if ($needWrite) {
        $json = @"
{
  "ControllerAddress": "$ControllerAddress",
  "AgentGrpcPort": $AgentPort,
  "Kestrel": {
    "EndpointDefaults": { "Protocols": "Http2" }
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
  }
}
"@
        [IO.File]::WriteAllText($appSettings, $json, (New-Object Text.UTF8Encoding($false)))
        Write-Ok "Wrote appsettings.json (controller $ControllerAddress, port $AgentPort)"
    } else {
        Write-Note 'appsettings.json already consistent (kept)'
    }
}

# ============================================================================
#  PHASE 5 - run-as account: local admin + logon rights (no password needed here)
# ============================================================================
function Grant-UserRight {
    param([string]$Sid, [string]$Right)
    $exp = Join-Path $env:TEMP ("ur_export_{0}.inf" -f ([guid]::NewGuid().ToString('N')))
    $cfg = Join-Path $env:TEMP ("ur_apply_{0}.inf"  -f ([guid]::NewGuid().ToString('N')))
    $db  = Join-Path $env:TEMP ("ur_apply_{0}.sdb"  -f ([guid]::NewGuid().ToString('N')))
    try {
        & secedit /export /cfg "$exp" /areas USER_RIGHTS *> $null
        $line = (Get-Content -LiteralPath $exp | Where-Object { $_ -match "^\s*$Right\s*=" } | Select-Object -First 1)
        if ($line) {
            $rhs = ($line -split '=',2)[1].Trim()
            if (@($rhs -split ',') | Where-Object { $_.Trim() -eq "*$Sid" }) { Write-Note "$Right already granted"; return }
            $rhs = "$rhs,*$Sid"
        } else { $rhs = "*$Sid" }
        @('[Unicode]','Unicode=yes','[Version]','signature="$CHICAGO$"','Revision=1','[Privilege Rights]',"$Right = $rhs") |
            Set-Content -LiteralPath $cfg -Encoding Unicode
        & secedit /configure /db "$db" /cfg "$cfg" /areas USER_RIGHTS *> $null
        Write-Ok "Granted '$Right' to $LogonUser"
    } catch { Add-Warn "Could not grant '$Right' ($($_.Exception.Message))." }
    finally  { Remove-Item -LiteralPath $exp,$cfg,$db -ErrorAction SilentlyContinue }
}

function Set-RunAsAccount {
    if (-not $LogonUser) { Add-Warn 'No run-as account supplied - run-as setup skipped.'; return }
    $sid = $null
    try { $sid = ([System.Security.Principal.NTAccount]$LogonUser).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
    if (-not $sid) { Add-Warn "Account '$LogonUser' does not resolve to a SID here - run-as setup SKIPPED. Use DOMAIN\user and re-run when the domain is reachable."; return }
    Write-Ok "Resolved $LogonUser (SID $sid)"

    $adminName  = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value
    $adminShort = $adminName.Split('\')[-1]
    $isMember = $false
    try { $isMember = [bool](Get-LocalGroupMember -Group $adminShort -Member $LogonUser -ErrorAction SilentlyContinue) } catch { }
    if ($isMember) { Write-Note "$LogonUser already in $adminShort" }
    else {
        $done = $false
        try { Add-LocalGroupMember -Group $adminShort -Member $LogonUser -ErrorAction Stop; $done = $true } catch { }
        if (-not $done) { & net localgroup "$adminShort" "$LogonUser" /add 2>$null; if ($LASTEXITCODE -eq 0) { $done = $true } }
        if ($done) { Write-Ok "Added $LogonUser to $adminShort" } else { Add-Warn "Could not add $LogonUser to $adminShort." }
    }
    Grant-UserRight -Sid $sid -Right 'SeServiceLogonRight'       # Log on as a service
    Grant-UserRight -Sid $sid -Right 'SeBatchLogonRight'         # Log on as a batch job
    Grant-UserRight -Sid $sid -Right 'SeInteractiveLogonRight'   # Log on locally (interactive desktop for UI tests)

    try { & icacls "$InstallDir" /grant "${LogonUser}:(OI)(CI)M" /T /C *> $null; Write-Ok "Granted $LogonUser modify rights on $InstallDir" } catch { }
    foreach ($d in 'C:\TestSetup','C:\TestBinaries','C:\TestResults') {
        try { & icacls "$d" /grant "${LogonUser}:(OI)(CI)M" /T /C *> $null } catch { }
    }
}

# ============================================================================
#  PHASE 6 - disable UAC (registry)
# ============================================================================
function Disable-UAC {
    $sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Set-RegValue $sys 'EnableLUA'                  0
    Set-RegValue $sys 'ConsentPromptBehaviorAdmin' 0
    Set-RegValue $sys 'PromptOnSecureDesktop'      0
    Set-RegValue $sys 'EnableInstallerDetection'   0
    Set-RegValue $sys 'FilterAdministratorToken'   0
    Write-Ok 'UAC disabled (reboot required to fully take effect).'
    $script:RebootNeeded = $true
}

# ============================================================================
#  PHASE 7 - trust controller/agent URLs + suppress Open-File security prompts
# ============================================================================
function Disable-SecurityPrompts {
    $zoneHosts = New-Object System.Collections.Generic.List[string]
    foreach ($h in @($env:COMPUTERNAME,'localhost','127.0.0.1',$script:ControllerHost)) { if ($h) { $zoneHosts.Add($h) | Out-Null } }
    try { $fqdn = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName; if ($fqdn) { $zoneHosts.Add($fqdn) | Out-Null } } catch { }
    foreach ($h in $ExtraTrustedHosts) { if ($h) { $zoneHosts.Add($h) | Out-Null } }
    $zoneHosts = $zoneHosts | Select-Object -Unique

    foreach ($base in @(
            'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMapKey',
            'HKCU:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMapKey')) {
        if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }
        foreach ($h in $zoneHosts) { foreach ($scheme in @('http','https')) {
            New-ItemProperty -Path $base -Name "${scheme}://${h}" -Value '1' -PropertyType String -Force | Out-Null
        } }
    }
    Write-Ok ("Added to Local Intranet zone: {0}" -f ($zoneHosts -join ', '))

    foreach ($hive in @('HKLM','HKCU')) {
        $att = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"
        Set-RegValue $att 'SaveZoneInformation'      1
        Set-RegValue $att 'HideZoneInfoOnProperties' 1
        Set-RegValue $att 'ScanWithAntiVirus'        1
        $assoc = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Associations"
        Set-RegValue $assoc 'DefaultFileTypeRisk' 0x1808
        Set-RegValue $assoc 'LowRiskFileTypes' '.exe;.bat;.cmd;.ps1;.psm1;.msi;.vbs;.reg;.dll;.com;.zip;.7z;' 'String'
        foreach ($z in 0,1,2,3) {
            $zk = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\$z"
            Set-RegValue $zk '1806' 0
            Set-RegValue $zk '1807' 0
        }
    }
    [Environment]::SetEnvironmentVariable('SEE_MASK_NOZONECHECKS','1','Machine')
    $env:SEE_MASK_NOZONECHECKS = '1'
    Write-Ok 'Attachment-Manager prompts disabled (machine + user); SEE_MASK_NOZONECHECKS set.'

    if (Test-Path $InstallDir) {
        Get-ChildItem -Path $InstallDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
        Write-Ok "Cleared Mark-of-the-Web under $InstallDir"
    }
}

# ============================================================================
#  PHASE 8 - credentials + scheduled task "TestAgentGrpc Interactive" (+ auto-logon)
# ============================================================================
function Ensure-Credential {
    # Returns a PSCredential or $null. Prompts (masked) unless -NoPrompt or one was passed.
    if ($LogonCredential) { return $LogonCredential }
    if ($NoPrompt)        { return $null }
    $u = if ($LogonUser) { $LogonUser } else { $null }
    try { return (Get-Credential -UserName $u -Message 'Run-as account for the TestAgentGrpc Interactive task (username + password)') }
    catch { return $null }
}

function Register-AgentTask {
    param([pscredential]$Cred)
    if (-not $Cred) { Add-Warn "No credential - scheduled task '$TaskName' not registered. Re-run with -LogonCredential or without -NoPrompt."; return }
    if (-not (Test-Path $AgentExePath)) { Add-Warn "Agent exe not at $AgentExePath yet - task will be registered but deploy the binaries before it can start." }

    $user  = $Cred.UserName
    $plain = Get-PlainFromSecure $Cred.Password
    try {
        $action   = New-ScheduledTaskAction -Execute $AgentExePath -WorkingDirectory $InstallDir
        $trigger  = New-ScheduledTaskTrigger -AtLogOn
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
                        -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -User $user -Password $plain -RunLevel Highest -Force | Out-Null
        Write-Ok "Scheduled task '$TaskName' registered to run as $user (AtLogon, interactive, highest)."
    } catch {
        Add-Warn "Could not register '$TaskName' ($($_.Exception.Message))."
    } finally {
        $plain = $null
    }
}

function Set-AutoLogon {
    param([pscredential]$Cred)
    if (-not $Cred) { return }
    # Prefer Sysinternals Autologon.exe: stores the password as an ENCRYPTED LSA secret, not plain text.
    $tool = $AutologonPath
    if (-not $tool) { foreach ($c in (Join-Path $PSScriptRoot 'Autologon.exe'),(Join-Path $InstallDir 'Autologon.exe')) { if (Test-Path $c) { $tool = $c; break } } }
    $user = $Cred.UserName; $dom = '.'
    if ($user -match '\\') { $dom,$user = $user.Split('\',2) }
    if ($tool -and (Test-Path $tool)) {
        $plain = Get-PlainFromSecure $Cred.Password
        try {
            & $tool /accepteula $user $dom $plain *> $null
            Write-Ok "Auto-logon set via Autologon.exe (password stored as an encrypted LSA secret)."
        } catch { Add-Warn "Autologon.exe failed ($($_.Exception.Message))." }
        finally { $plain = $null }
    } else {
        Add-Warn 'Autologon.exe not found. Auto-logon NOT set. Download Sysinternals Autologon, place it next to this script (or pass -AutologonPath), and re-run with -EnableAutoLogon. (Avoid the plain-text DefaultPassword registry method.)'
    }
}

# ============================================================================
#  PHASE 9 - start the agent + verify
# ============================================================================
function Start-AndVerify {
    if (-not (Test-Path $AgentExePath)) { Add-Warn "Agent exe not deployed yet - skipping start. Copy the published agent to $InstallDir, then start the task or reboot."; return }
    try {
        $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($AgentExeName)) -ErrorAction SilentlyContinue
        if (-not $running) {
            if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue; Write-Note "Started task '$TaskName'." }
            else { Start-Process -FilePath $AgentExePath -WorkingDirectory $InstallDir; Write-Note 'Launched agent exe directly.' }
        } else { Write-Note 'Agent already running.' }
    } catch { Add-Warn "Could not start the agent ($($_.Exception.Message))." }

    $listening = $false
    for ($i=0; $i -lt 8 -and -not $listening; $i++) {
        Start-Sleep -Seconds 2
        $listening = [bool](Get-NetTCPConnection -LocalPort $AgentPort -State Listen -ErrorAction SilentlyContinue)
    }
    if ($listening) { Write-Ok "Agent is LISTENING on port $AgentPort." }
    else { Add-Warn "Agent not listening on port $AgentPort yet. If UAC/auto-logon was just set, reboot so the interactive task starts the agent on the desktop." }
}

# ============================================================================
#  MAIN
# ============================================================================
Write-Host ''
Write-Host '  ============================================================' -ForegroundColor Green
Write-Host '   TestAgent AGENT NODE PREP  (self-hosted gRPC, self-healing)' -ForegroundColor Green
Write-Host ('   Host: {0}  Agent port: {1}  Controller: {2}  .NET {3}+' -f $env:COMPUTERNAME, $AgentPort, $ControllerAddress, $script:DotNetMinVersion) -ForegroundColor Green
Write-Host '  ============================================================' -ForegroundColor Green

# Ask for the run-as credential up front (masked) so phases 5 and 8 can use it.
$cred = $null
if (-not $SkipRunAsAccount -or -not $NoPrompt) { $cred = Ensure-Credential }
if ($cred -and -not $LogonUser) { $LogonUser = $cred.UserName }

Write-Phase 'Folders'
try { Ensure-Folders } catch { Add-Warn "Folders phase: $($_.Exception.Message)" }

Write-Phase 'Installing / repairing .NET 10 runtimes (ASP.NET Core + Windows Desktop + Core; x64 + x86)'
if ($SkipDotNet) { Write-Note 'Skipped (-SkipDotNet)' } else { try { Ensure-DotNetRuntime } catch { Add-Warn "DotNet phase: $($_.Exception.Message)" } }

Write-Phase 'Firewall allow rules (agent in / controller out / exe / ping)'
if ($SkipFirewall) { Write-Note 'Skipped (-SkipFirewall)' } else { try { Set-Networking } catch { Add-Warn "Networking phase: $($_.Exception.Message)" } }

Write-Phase 'appsettings.json (controller address + agent port)'
try { Ensure-AgentConfig } catch { Add-Warn "Config phase: $($_.Exception.Message)" }

Write-Phase 'Run-as account (local admin + logon rights)'
if ($SkipRunAsAccount) { Write-Note 'Skipped (-SkipRunAsAccount)' } else { try { Set-RunAsAccount } catch { Add-Warn "Run-as phase: $($_.Exception.Message)" } }

Write-Phase 'Disabling UAC'
if ($SkipUacDisable) { Write-Note 'Skipped (-SkipUacDisable)' } else { try { Disable-UAC } catch { Add-Warn "UAC phase: $($_.Exception.Message)" } }

Write-Phase 'Trusting controller/agent URLs + suppressing Open-File security prompts'
if ($SkipSecurityPrompts) { Write-Note 'Skipped (-SkipSecurityPrompts)' } else { try { Disable-SecurityPrompts } catch { Add-Warn "Security-prompt phase: $($_.Exception.Message)" } }

Write-Phase 'Scheduled task + auto-logon (uses the run-as credential)'
try {
    Register-AgentTask -Cred $cred
    if ($EnableAutoLogon) { Set-AutoLogon -Cred $cred } else { Write-Note 'Auto-logon not requested (-EnableAutoLogon to enable).' }
} catch { Add-Warn "Task phase: $($_.Exception.Message)" }

Write-Phase 'Start the agent + verify port'
if ($SkipStart) { Write-Note 'Skipped (-SkipStart)' } else { try { Start-AndVerify } catch { Add-Warn "Start phase: $($_.Exception.Message)" } }

# ---- summary ----
Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ' AGENT PREP COMPLETE' -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host (' Install dir : {0}' -f $InstallDir)
Write-Host (' Agent port  : {0} (controller dispatches here)' -f $AgentPort)
Write-Host (' Controller  : {0}' -f $ControllerAddress)
Write-Host (' Run-as      : {0}' -f $(if ($LogonUser) { $LogonUser } else { '(none)' }))
Write-Host (' Task        : {0}' -f $TaskName)
Write-Host ''
if ($script:Warnings.Count) {
    Write-Host (' {0} warning(s):' -f $script:Warnings.Count) -ForegroundColor Yellow
    foreach ($w in $script:Warnings) { Write-Host "   - $w" -ForegroundColor Yellow }
} else { Write-Host ' No warnings.' -ForegroundColor Green }
Write-Host ''
Write-Host ' NEXT:' -ForegroundColor Yellow
Write-Host ("   1. Copy the published agent binaries to {0} (if not already there)." -f $InstallDir)
Write-Host '   2. Reboot so UAC-off + the interactive logon task bring the agent up on the desktop.'
Write-Host ("   3. On the controller, confirm {0} appears online in the Fleet panel." -f $env:COMPUTERNAME)
Write-Host ''

if ($script:RebootNeeded) {
    if ($RebootWhenDone) {
        Write-Host 'Rebooting in 10 seconds (Ctrl+C to cancel)...' -ForegroundColor Yellow
        Start-Sleep -Seconds 10
        try { Stop-Transcript | Out-Null } catch { }
        Restart-Computer -Force
    } else {
        Write-Host 'A reboot is required to finish activating UAC-off and the interactive agent task. Re-run with -RebootWhenDone, or reboot manually.' -ForegroundColor Yellow
    }
}

try { Stop-Transcript | Out-Null } catch { }
exit $(if ($script:Warnings.Count) { 2 } else { 0 })
