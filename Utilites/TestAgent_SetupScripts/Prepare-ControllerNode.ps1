#Requires -Version 5.1
<#
.SYNOPSIS
    TestAgent CONTROLLER Node - one-click, self-healing prep for the
    TestAgentSolution controller (TestControllerGrpc WPF gRPC server +
    TestController.WebApi on IIS: REST + SignalR + React, .NET 10).

.DESCRIPTION
    Run ONCE on a controller machine to make it ready. Safe to re-run any time:
    every phase checks what is already in place and only fills the gaps
    (idempotent). The script self-elevates. There is NO auto-logon here - the
    controller is launched interactively by a person, and/or hosted in IIS.

    Phases (run in this order so the Hosting Bundle can register the IIS module):

      [1] IIS + required features + appcmd ....... Enable-IISFeatures
          (incl. WebSockets for SignalR, Mgmt Tools for appcmd)
      [2] .NET 10 x64+x86 in one shot ............ Ensure-DotNetRuntime
          ASP.NET Core Hosting Bundle (IIS/ANCM) + Windows Desktop (WPF) + .NET Core
      [3] Firewall (controller in / agents out) .. Set-Networking
      [4] Run-as account (wwApps) ................ Set-RunAsAccount
          local admin + "log on as a service/batch"; optional IIS app-pool identity
      [5] Dirs + appsettings.json + WatchList.xml  Ensure-ControllerConfig
      [6] Disable UAC (registry) ................. Disable-UAC
      [7] Trust controller/agent URLs ........... Disable-SecurityPrompts

    Run-as: the controller (IIS app pool and/or the WPF exe) is set up to run as
    an Administrator or as magellandev2000\wwApps, which is granted local admin
    plus the logon rights an app-pool / service identity needs. No password is
    needed for the default run; it is only requested when you also pass
    -ConfigureAppPoolIdentity (to stamp an existing IIS app pool with wwApps).

.NOTES
    LAB / TEST posture: disables UAC and relaxes SmartScreen / zone checks so the
    controller and its tooling run unattended. On a daily-driver workstation pass
    -SkipUacDisable (and -SkipSecurityPrompts) to keep those protections.
    appcmd.exe is used exclusively for IIS; this script only ENSURES IIS + appcmd
    exist - actual site/app-pool creation is done by Deploy-TestController.ps1.
    Version 1.0
#>

param(
    # --- Controller identity / ports ---
    [int]      $ControllerPort      = 5100,                       # agents -> controller (gRPC register/heartbeat/events)
    [int]      $AgentPort           = 5200,                       # controller -> agents (gRPC command dispatch)
    [string[]] $AgentIPs            = @(),                        # pre-register agents, e.g. @('10.48.190.213','10.228.117.101')
    [int[]]    $ExtraInboundPorts   = @(),                        # extra inbound ports to open (e.g. the WebApi/React http binding)

    # --- Paths ---
    [string]   $InstallDir          = 'C:\TestControllerService', # where TestControllerGrpc.exe + appsettings.json live
    [string]   $VocabularyDir       = 'C:\TestControllerService', # where WatchList.xml + Logs live
    [string]   $ControllerExeName   = 'TestControllerGrpc.exe',

    # --- Run-as account (no auto-logon) ---
    [string]   $LogonUser           = 'magellandev2000\wwApps',
    [string]   $LogonPassword,                                    # only needed with -ConfigureAppPoolIdentity
    [switch]   $ConfigureAppPoolIdentity,                         # stamp an existing IIS app pool with the run-as account
    [string]   $AppPoolName         = 'TestControllerPool',

    # --- .NET 10 ---
    [string]   $DotNetInstallDir    = 'C:\Program Files\dotnet',  # x64 shared-framework root (installer-managed)
    [ValidateSet('x64','x86')]
    [string[]] $DotNetArchitectures = @('x64','x86'),             # both, so 32- and 64-bit apps each resolve a runtime
    [switch]   $UseStandaloneAspNet,                              # default = ASP.NET Core Hosting Bundle (IIS needs the ANCM module)
    [switch]   $SkipNetCore,                                      # default = also install the lean .NET Core runtime
    [string]   $ReleaseMetadataUrl,                               # override the .NET release feed
    [string]   $OfflineInstallerDir,                              # folder of pre-staged .exe installers (air-gapped boxes)

    # --- Zone-relaxation trusted hosts ---
    [string]   $ControllerHost      = 'rcloud.dev.wonderware.com',
    [string[]] $ExtraTrustedHosts   = @(),

    # --- Logging ---
    [string]   $LogFile             = 'C:\TestControllerService\Logs\Prepare-ControllerNode.log',

    # --- Phase skips / modes ---
    [switch]   $SkipIIS,
    [switch]   $SkipDotNet,
    [switch]   $SkipFirewall,
    [switch]   $SkipRunAsAccount,
    [switch]   $SkipUacDisable,
    [switch]   $SkipSecurityPrompts,
    [switch]   $Uninstall,
    [switch]   $RebootWhenDone,
    [switch]   $NoElevate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # makes Invoke-WebRequest downloads fast under PS 5.1

$script:DotNetMajor   = 10
$ControllerExePath    = Join-Path $InstallDir $ControllerExeName
$UseHostingBundle     = -not $UseStandaloneAspNet    # IIS-hosted WebApi needs ANCM -> hosting bundle by default
$IncludeNetCore       = -not $SkipNetCore            # install the lean base runtime as well, in one shot
$script:Warnings      = New-Object System.Collections.Generic.List[string]
$script:RebootNeeded  = $false
$script:Phase         = 0
$script:TotalPhases   = 7
$script:AncmDll       = Join-Path $env:windir 'system32\inetsrv\aspnetcorev2.dll'
$script:NeedAncm      = $false
$script:PlainPwd      = $null
$script:LocalIP       = $null

# ----------------------------------------------------------------------------
#  Console helpers (ASCII-only so they render under PS 5.1)
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

function Get-LogonPassword {
    if ($script:PlainPwd) { return $script:PlainPwd }
    if ($LogonPassword)   { $script:PlainPwd = $LogonPassword; return $script:PlainPwd }
    $sec  = Read-Host "Enter password for $LogonUser" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try   { $script:PlainPwd = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    return $script:PlainPwd
}

# ----------------------------------------------------------------------------
#  Self-elevate (forward NON-SECRET parameters only; if a password is needed the
#  elevated instance prompts for it so it never lands in a command line)
# ----------------------------------------------------------------------------
if (-not (Test-Admin)) {
    if ($NoElevate)          { Write-Err 'Not elevated and -NoElevate was specified. Re-run as Administrator.'; exit 1 }
    if (-not $PSCommandPath)  { Write-Err 'Cannot self-elevate (no script path). Run from an elevated PowerShell prompt.'; exit 1 }

    Write-Host 'Not running as Administrator - relaunching elevated...' -ForegroundColor Yellow
    $fwd = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"",
        '-ControllerPort',      $ControllerPort,
        '-AgentPort',           $AgentPort,
        '-InstallDir',         "`"$InstallDir`"",
        '-VocabularyDir',      "`"$VocabularyDir`"",
        '-ControllerExeName',  "`"$ControllerExeName`"",
        '-LogonUser',          "`"$LogonUser`"",
        '-AppPoolName',        "`"$AppPoolName`"",
        '-DotNetInstallDir',   "`"$DotNetInstallDir`"",
        '-DotNetArchitectures', ($DotNetArchitectures -join ','),
        '-ControllerHost',     "`"$ControllerHost`"",
        '-LogFile',            "`"$LogFile`""
    )
    if ($AgentIPs.Count)          { $fwd += @('-AgentIPs',          ($AgentIPs -join ',')) }
    if ($ExtraInboundPorts.Count) { $fwd += @('-ExtraInboundPorts', ($ExtraInboundPorts -join ',')) }
    if ($ExtraTrustedHosts.Count) { $fwd += @('-ExtraTrustedHosts', ($ExtraTrustedHosts -join ',')) }
    if ($ReleaseMetadataUrl)      { $fwd += @('-ReleaseMetadataUrl', "`"$ReleaseMetadataUrl`"") }
    if ($OfflineInstallerDir)     { $fwd += @('-OfflineInstallerDir', "`"$OfflineInstallerDir`"") }
    if ($UseStandaloneAspNet)     { $fwd += '-UseStandaloneAspNet' }
    if ($SkipNetCore)             { $fwd += '-SkipNetCore' }
    if ($ConfigureAppPoolIdentity){ $fwd += '-ConfigureAppPoolIdentity' }
    if ($SkipIIS)                 { $fwd += '-SkipIIS' }
    if ($SkipDotNet)              { $fwd += '-SkipDotNet' }
    if ($SkipFirewall)            { $fwd += '-SkipFirewall' }
    if ($SkipRunAsAccount)        { $fwd += '-SkipRunAsAccount' }
    if ($SkipUacDisable)          { $fwd += '-SkipUacDisable' }
    if ($SkipSecurityPrompts)     { $fwd += '-SkipSecurityPrompts' }
    if ($Uninstall)               { $fwd += '-Uninstall' }
    if ($RebootWhenDone)          { $fwd += '-RebootWhenDone' }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $fwd
    exit
}

# Logs dir + transcript (after elevation so the file lands once)
try { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null } catch { }
try { Start-Transcript -Path $LogFile -Append -ErrorAction Stop | Out-Null }            catch { }

# ----------------------------------------------------------------------------
#  Uninstall mode: remove the firewall rules this script adds (files + IIS left).
# ----------------------------------------------------------------------------
if ($Uninstall) {
    Write-Host ''
    Write-Host 'Removing TestController firewall rules...' -ForegroundColor Yellow
    $removed = 0
    Get-NetFirewallRule -DisplayName 'TestController*' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-NetFirewallRule -Name $_.Name -ErrorAction SilentlyContinue
        Write-Host ("  Removed: {0}" -f $_.DisplayName) -ForegroundColor Green
        $removed++
    }
    if ($removed -eq 0) { Write-Host '  No TestController firewall rules found.' -ForegroundColor Gray }
    Write-Host ''
    Write-Host 'Application files, IIS features and runtimes were NOT removed.' -ForegroundColor Yellow
    Write-Host ("  To remove files:  Remove-Item -Recurse -Force '{0}'" -f $InstallDir) -ForegroundColor Gray
    try { Stop-Transcript | Out-Null } catch { }
    exit 0
}

# ============================================================================
#  .NET runtime engine (official MSI redistributables; reference-counted so they
#  coexist with any product that also installs .NET; per-architecture detection)
# ============================================================================
function Test-SharedFx {
    param([string]$DotnetBase, [string]$Framework, [int]$Major)
    $dir = Join-Path $DotnetBase "shared\$Framework"
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    [bool](Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -like "$Major.*" } | Select-Object -First 1)
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
    param($FileList, [string]$Rid, [switch]$HostingBundle, [switch]$CoreRuntime)
    if ($HostingBundle) {
        $FileList | Where-Object { $_.name -like 'dotnet-hosting-*win.exe' } | Select-Object -First 1
    } elseif ($CoreRuntime) {
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
        $proc = Start-Process -FilePath $local -ArgumentList @('/install','/quiet','/norestart','/log', "`"$log`"") -Wait -PassThru -ErrorAction Stop
        switch ($proc.ExitCode) {
            0       { Write-Note "$Label installed (exit 0)" }
            3010    { Write-Note "$Label installed (exit 3010 - reboot pending)"; $script:RebootNeeded = $true }
            1641    { Write-Note "$Label installed (exit 1641 - reboot)";         $script:RebootNeeded = $true }
            1638    { Write-Note "$Label : equal/newer version already present (exit 1638) - coexisting" }
            default { Write-Note "$Label installer exit code $($proc.ExitCode) - verifying on disk" }
        }
    } catch { Add-Warn "$Label : installer failed to launch ($($_.Exception.Message))." }
}

# ============================================================================
#  PHASE 1 - IIS + required features + appcmd
# ============================================================================
#  The WebApi is hosted in IIS, so we need the web server, the management tools
#  (appcmd.exe, used exclusively by the deploy script), static content + default
#  document (to serve the React WebClient), and WebSockets (so SignalR can use
#  the WebSocket transport through IIS). Works on client (DISM) and server
#  (ServerManager) SKUs. IIS is enabled BEFORE the runtimes so the ASP.NET Core
#  Hosting Bundle can register its IIS module (ANCM).
function Enable-IISFeatures {
    $isServer = $false
    try { $isServer = ((Get-CimInstance Win32_OperatingSystem).ProductType -ne 1) } catch { }

    if ($isServer -and (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) {
        $roles = @(
            'Web-Server','Web-WebServer','Web-Common-Http','Web-Default-Doc','Web-Dir-Browsing',
            'Web-Http-Errors','Web-Static-Content','Web-Http-Redirect','Web-Health','Web-Http-Logging',
            'Web-Performance','Web-Stat-Compression','Web-Security','Web-Filtering','Web-Windows-Auth',
            'Web-AppInit','Web-WebSockets','Web-Mgmt-Tools','Web-Mgmt-Console','Web-Scripting-Tools'
        )
        Write-Note 'Installing IIS roles via Server Manager...'
        try {
            $r = Install-WindowsFeature -Name $roles -IncludeManagementTools -ErrorAction Stop
            if ($r.RestartNeeded -and "$($r.RestartNeeded)" -ne 'No') { $script:RebootNeeded = $true }
            Write-Ok 'IIS roles ensured (Server Manager)'
        } catch { Add-Warn "IIS role install failed: $($_.Exception.Message)" }
    } else {
        $feats = @(
            'IIS-WebServerRole','IIS-WebServer','IIS-CommonHttpFeatures','IIS-DefaultDocument',
            'IIS-DirectoryBrowsing','IIS-HttpErrors','IIS-StaticContent','IIS-HttpRedirect',
            'IIS-HealthAndDiagnostics','IIS-HttpLogging','IIS-Performance','IIS-HttpCompressionStatic',
            'IIS-Security','IIS-RequestFiltering','IIS-WindowsAuthentication','IIS-ApplicationInit',
            'IIS-WebSockets','IIS-WebServerManagementTools','IIS-ManagementConsole','IIS-ManagementScriptingTools'
        )
        Write-Note 'Enabling IIS features via DISM...'
        $changed = 0
        foreach ($f in $feats) {
            try {
                $state = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction Stop).State
                if ($state -ne 'Enabled') {
                    $res = Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart -ErrorAction Stop
                    if ($res.RestartNeeded) { $script:RebootNeeded = $true }
                    $changed++
                }
            } catch { Write-Note "Feature not applicable/failed: $f" }
        }
        Write-Ok ("IIS features ensured (DISM){0}" -f $(if ($changed) { " - $changed newly enabled" } else { '' }))
    }

    # appcmd present? (deploy script uses it exclusively)
    $appcmd = Join-Path $env:windir 'system32\inetsrv\appcmd.exe'
    if (Test-Path $appcmd) { Write-Ok "appcmd available: $appcmd" }
    else { Add-Warn 'appcmd.exe not found - IIS Management Tools may not be installed yet; the deploy script needs it (a reboot may be required to finish IIS install).' }

    # W3SVC running + automatic
    try {
        $w3 = Get-Service W3SVC -ErrorAction Stop
        if ($w3.StartType -ne 'Automatic') { Set-Service W3SVC -StartupType Automatic -ErrorAction SilentlyContinue }
        if ($w3.Status -ne 'Running')      { Start-Service W3SVC -ErrorAction SilentlyContinue }
        Write-Ok 'World Wide Web Publishing Service (W3SVC) present'
    } catch { Write-Note 'W3SVC not present yet (a reboot may be required to finish IIS install).' }

    # Does the ASP.NET Core IIS module (ANCM) still need installing via the bundle?
    $script:NeedAncm = $UseHostingBundle -and -not (Test-Path $script:AncmDll)
    if ($script:NeedAncm) { Write-Note 'ASP.NET Core IIS module (ANCM) not present yet - the Hosting Bundle in phase 2 will register it.' }
}

# ============================================================================
#  PHASE 2 - .NET 10 runtimes in one shot (Hosting Bundle + Desktop + Core)
# ============================================================================
function Ensure-DotNetRuntime {
    $major   = $script:DotNetMajor
    $channel = "$major.0"
    $baseFor = @{ 'x64' = $DotNetInstallDir; 'x86' = (Join-Path ${env:ProgramFiles(x86)} 'dotnet') }
    $needAncm = [bool]$script:NeedAncm

    # 1) Per-architecture gaps, straight from the shared-framework folders on disk.
    $needAsp = @{}; $needDesk = @{}; $needCore = @{}
    foreach ($arch in $DotNetArchitectures) {
        $needAsp[$arch]  = -not (Test-SharedFx $baseFor[$arch] 'Microsoft.AspNetCore.App'    $major)
        $needDesk[$arch] = -not (Test-SharedFx $baseFor[$arch] 'Microsoft.WindowsDesktop.App' $major)
        $needCore[$arch] = $IncludeNetCore -and (-not (Test-SharedFx $baseFor[$arch] 'Microsoft.NETCore.App' $major))
    }
    if (-not (($needAsp.Values -contains $true) -or ($needDesk.Values -contains $true) -or ($needCore.Values -contains $true) -or $needAncm)) {
        $tail = if ($IncludeNetCore) { ' + .NET Core' } else { '' }
        Write-Ok ".NET $major (ASP.NET Core + Windows Desktop$tail + ANCM) already present for: $($DotNetArchitectures -join ', ')"
        return
    }
    foreach ($arch in $DotNetArchitectures) {
        $miss = @()
        if ($needAsp[$arch])  { $miss += 'ASP.NET-Core' }
        if ($needDesk[$arch]) { $miss += 'Windows-Desktop' }
        if ($needCore[$arch]) { $miss += '.NET-Core' }
        if ($miss) { Write-Note ("Missing for {0}: {1}" -f $arch, ($miss -join ' + ')) }
    }
    if ($needAncm) { Write-Note 'ASP.NET Core IIS module (ANCM) needs (re)install via the Hosting Bundle.' }

    # 2) Resolve the exact latest 10.x installers from the official release feed.
    $primary = if ($ReleaseMetadataUrl) { $ReleaseMetadataUrl } else { "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" }
    $legacy  = "https://dotnetcli.azureedge.net/dotnet/release-metadata/$channel/releases.json"
    $meta = Get-DotNetReleaseInfo -Urls @($primary, $legacy)
    if (-not $meta) {
        Add-Warn "Could not reach the .NET release feed. Pre-stage installers in -OfflineInstallerDir, or install the .NET $channel ASP.NET Core Hosting Bundle + Windows Desktop runtime (x64 AND x86) manually from https://dotnet.microsoft.com/download/dotnet/$channel"
        return
    }
    $rel = $meta.releases | Where-Object { $_.'release-version' -eq $meta.'latest-release' } | Select-Object -First 1
    if (-not $rel) { $rel = $meta.releases | Select-Object -First 1 }
    $aspFiles  = $rel.'aspnetcore-runtime'.files
    $deskFiles = $rel.windowsdesktop.files
    $coreFiles = $rel.runtime.files
    Write-Note "Target build: .NET $($rel.'release-version')"

    # 3) ASP.NET Core. The WebApi is IIS-hosted, so by default use the Hosting
    #    Bundle: one .exe covering BOTH architectures + the IIS module (ANCM).
    $aspNeededArches = @($DotNetArchitectures | Where-Object { $needAsp[$_] })
    if ($aspNeededArches.Count -or $needAncm) {
        if ($UseHostingBundle) {
            Install-RuntimeExe -Label "ASP.NET Core Hosting Bundle (x64+x86, ANCM) $channel" -FileEntry (Get-FileFromRelease $aspFiles -HostingBundle)
        } else {
            Add-Warn 'Standalone ASP.NET Core selected (-UseStandaloneAspNet): this does NOT install the IIS module (ANCM); IIS hosting of the WebApi will not work until the Hosting Bundle is installed.'
            foreach ($arch in $aspNeededArches) {
                Install-RuntimeExe -Label "ASP.NET Core Runtime $arch $channel" -FileEntry (Get-FileFromRelease $aspFiles -Rid "win-$arch")
            }
        }
    }

    # 4) Windows Desktop (the WPF controller) - one .exe per architecture.
    foreach ($arch in @($DotNetArchitectures | Where-Object { $needDesk[$_] })) {
        Install-RuntimeExe -Label "Windows Desktop Runtime $arch $channel" -FileEntry (Get-FileFromRelease $deskFiles -Rid "win-$arch")
    }

    # 4b) .NET Core base runtime (lean; no ASP.NET / WPF). Only when -IncludeNetCore.
    foreach ($arch in @($DotNetArchitectures | Where-Object { $needCore[$_] })) {
        Install-RuntimeExe -Label ".NET Core Runtime $arch $channel" -FileEntry (Get-FileFromRelease $coreFiles -Rid "win-$arch" -CoreRuntime)
    }

    # 5) Outcome check: present => OK regardless of installer exit code.
    foreach ($arch in $DotNetArchitectures) {
        if (Test-SharedFx $baseFor[$arch] 'Microsoft.AspNetCore.App' $major)    { Write-Ok "ASP.NET Core $major ($arch) ready" }    elseif ($needAsp[$arch])  { Add-Warn "ASP.NET Core $major ($arch) still missing after install." }
        if (Test-SharedFx $baseFor[$arch] 'Microsoft.WindowsDesktop.App' $major) { Write-Ok "Windows Desktop $major ($arch) ready" } elseif ($needDesk[$arch]) { Add-Warn "Windows Desktop $major ($arch) still missing after install." }
        if ($IncludeNetCore) {
            if (Test-SharedFx $baseFor[$arch] 'Microsoft.NETCore.App' $major)    { Write-Ok ".NET Core $major ($arch) ready" }       elseif ($needCore[$arch]) { Add-Warn ".NET Core $major ($arch) still missing after install." }
        }
    }
    if ($UseHostingBundle -and -not $SkipIIS) {
        if (Test-Path $script:AncmDll) { Write-Ok 'ASP.NET Core IIS module (ANCM) registered' }
        else { Add-Warn 'ANCM (aspnetcorev2.dll) not found after the Hosting Bundle install; IIS hosting may fail. Make sure IIS is enabled, then re-run.' }
    }
}

# ============================================================================
#  PHASE 3 - firewall (controller in / agents out / per-agent)
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
function New-FwProgramRule {
    param([string]$DisplayName, [string]$Direction, [string]$Program)
    if (Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue) { Write-Note "FW exists: $DisplayName"; return }
    New-NetFirewallRule -DisplayName $DisplayName -Direction $Direction -Program $Program -Action Allow -Enabled True -Profile Any | Out-Null
    Write-Ok "FW created: $DisplayName"
}
function Set-Networking {
    New-FwPortRule -DisplayName "TestController Inbound gRPC/REST/SignalR (TCP $ControllerPort)" -Direction Inbound  -LocalPort  $ControllerPort -Desc 'Agents -> controller: registration, heartbeat, event push, REST, SignalR'
    New-FwPortRule -DisplayName "TestController to Agents Outbound (TCP $AgentPort)"             -Direction Outbound -RemotePort $AgentPort      -Desc 'Controller -> agents: gRPC command dispatch'

    foreach ($p in $ExtraInboundPorts) {
        New-FwPortRule -DisplayName "TestController Extra Inbound (TCP $p)" -Direction Inbound -LocalPort $p -Desc 'Additional controller / WebApi / React http binding'
    }

    foreach ($ip in $AgentIPs) {
        $n = "TestController Allow Agent $ip (TCP $AgentPort)"
        if (-not (Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $n -Description "Explicit allow to agent $ip" -Direction Outbound -Protocol TCP -RemoteAddress $ip -RemotePort $AgentPort -Action Allow -Profile Any -Enabled True | Out-Null
            Write-Ok "FW per-agent: $ip`:$AgentPort"
        }
    }

    if (Test-Path $ControllerExePath) {
        New-FwProgramRule -DisplayName "TestController app inbound ($ControllerExeName)"  -Direction Inbound  -Program $ControllerExePath
        New-FwProgramRule -DisplayName "TestController app outbound ($ControllerExeName)" -Direction Outbound -Program $ControllerExePath
    } else {
        Add-Warn "Controller exe not present yet at $ControllerExePath - its program firewall rule will be added on the next run after binaries are deployed."
    }
}

# ============================================================================
#  PHASE 4 - run-as account (wwApps): local admin + logon rights (no auto-logon)
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
            $rhs = ($line -split '=', 2)[1].Trim()
            if (@($rhs -split ',') | Where-Object { $_.Trim() -eq "*$Sid" }) { Write-Note "$Right already granted to $LogonUser"; return }
            $rhs = "$rhs,*$Sid"
        } else {
            $rhs = "*$Sid"
        }
        @(
            '[Unicode]'
            'Unicode=yes'
            '[Version]'
            'signature="$CHICAGO$"'
            'Revision=1'
            '[Privilege Rights]'
            "$Right = $rhs"
        ) | Set-Content -LiteralPath $cfg -Encoding Unicode
        & secedit /configure /db "$db" /cfg "$cfg" /areas USER_RIGHTS *> $null
        Write-Ok "Granted '$Right' to $LogonUser"
    } catch {
        Add-Warn "Could not grant '$Right' ($($_.Exception.Message))."
    } finally {
        Remove-Item -LiteralPath $exp, $cfg, $db -ErrorAction SilentlyContinue
    }
}

function Set-RunAsAccount {
    # Resolve the account to a SID first (clear message if the domain is unreachable).
    $sid = $null
    try { $sid = ([System.Security.Principal.NTAccount]$LogonUser).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
    if (-not $sid) {
        Add-Warn "Account '$LogonUser' does not resolve to a SID here - run-as setup SKIPPED. The controller can still run as a local Administrator. Use DOMAIN\user and re-run when the domain is reachable."
        return
    }
    Write-Ok "Resolved $LogonUser (SID $sid)"

    # 1) Local Administrators membership (locale-independent group name via SID).
    $adminName  = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value
    $adminShort = $adminName.Split('\')[-1]
    $isMember = $false
    try { $isMember = [bool](Get-LocalGroupMember -Group $adminShort -Member $LogonUser -ErrorAction SilentlyContinue) } catch { }
    if ($isMember) {
        Write-Note "$LogonUser already in $adminShort"
    } else {
        $done = $false
        try { Add-LocalGroupMember -Group $adminShort -Member $LogonUser -ErrorAction Stop; $done = $true } catch { }
        if (-not $done) { & net localgroup "$adminShort" "$LogonUser" /add 2>$null; if ($LASTEXITCODE -eq 0) { $done = $true } }
        if ($done) { Write-Ok "Added $LogonUser to $adminShort" } else { Add-Warn "Could not add $LogonUser to $adminShort." }
    }

    # 2) The logon rights an IIS app-pool / Windows-service identity needs.
    Grant-UserRight -Sid $sid -Right 'SeServiceLogonRight'   # Log on as a service
    Grant-UserRight -Sid $sid -Right 'SeBatchLogonRight'     # Log on as a batch job

    # 3) Optional: stamp an EXISTING IIS app pool with this identity (needs password).
    if ($ConfigureAppPoolIdentity) {
        $appcmd = Join-Path $env:windir 'system32\inetsrv\appcmd.exe'
        if (-not (Test-Path $appcmd)) { Add-Warn 'appcmd.exe missing - cannot set the app pool identity.'; return }
        $exists = (& $appcmd list apppool /name:"$AppPoolName" 2>$null)
        if (-not $exists) {
            Write-Note "App pool '$AppPoolName' not found yet - run Deploy-TestController.ps1 first, then re-run with -ConfigureAppPoolIdentity."
            return
        }
        $pwd = Get-LogonPassword
        if (-not $pwd) { Add-Warn 'No password supplied - cannot set the custom app pool identity.'; return }
        & $appcmd set apppool "$AppPoolName" /processModel.identityType:SpecificUser /processModel.userName:"$LogonUser" /processModel.password:"$pwd" *> $null
        if ($LASTEXITCODE -eq 0) { Write-Ok "App pool '$AppPoolName' now runs as $LogonUser" }
        else { Add-Warn "Failed to set app pool identity (appcmd exit $LASTEXITCODE)." }
    }
}

# ============================================================================
#  PHASE 5 - directories + appsettings.json + WatchList.xml (+ ACLs)
# ============================================================================
function Write-ControllerAppSettings {
    param([string]$Path, [int]$Port, [string]$VocabFile, $Agents)
    $vf    = $VocabFile.Replace('\', '\\')
    $items = foreach ($a in $Agents) { '    { "Name": "' + $a.Name + '", "Address": "' + $a.Address + '" }' }
    $agentsJson = ($items -join ",`r`n")
    $json = @"
{
  "VocabularyFile": "$vf",
  "ControllerGrpcPort": $Port,
  "Agents": [
$agentsJson
  ],
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
"@
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

function New-StarterWatchList {
    param([string]$Path)
    $xml = @'
<?xml version="1.0" encoding="utf-8"?>
<WatchList>
  <!-- Example WatchItem: watches C:\Triggers for renamed .txt files -->
  <WatchItem Tag="ExampleTrigger" Path="C:\Triggers" Filter="*.txt" IsEnabled="false">
    <Event Type="Renamed" ExecutionType="Sequential">
      <Action Type="RunCommand"
              Tag="LocalEcho"
              Command="cmd.exe"
              Parameters="/c echo Triggered by [FileName] at [DateTime]"
              Timeout="30"
              FailAndContinue="true" />
    </Event>
  </WatchItem>

  <Templates>
    <!-- Define reusable action groups here -->
  </Templates>
</WatchList>
'@
    [IO.File]::WriteAllText($Path, $xml, (New-Object Text.UTF8Encoding($false)))
}

function Ensure-ControllerConfig {
    foreach ($d in @($InstallDir, $VocabularyDir, (Join-Path $VocabularyDir 'Logs'))) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null; Write-Ok "Created dir: $d" }
        else { Write-Note "Dir exists: $d" }
    }

    $appSettings = Join-Path $InstallDir 'appsettings.json'
    $vocabFile   = Join-Path $VocabularyDir 'WatchList.xml'

    # Read existing agents/port (if any) so a re-run heals rather than clobbers.
    $existingAgents = @()
    $port      = $ControllerPort
    $needWrite = $false
    if (Test-Path $appSettings) {
        try {
            $cfg = Get-Content -Raw -LiteralPath $appSettings | ConvertFrom-Json
            if ($cfg.Agents) { $existingAgents = @($cfg.Agents | ForEach-Object { @{ Name = $_.Name; Address = $_.Address } }) }
            if ($cfg.ControllerGrpcPort) { $port = [int]$cfg.ControllerGrpcPort }
            if ((-not $cfg.ControllerGrpcPort) -or (-not $cfg.VocabularyFile)) { $needWrite = $true }
        } catch {
            Copy-Item -LiteralPath $appSettings -Destination "$appSettings.bak" -Force
            Add-Warn "Existing appsettings.json was invalid - backed up to appsettings.json.bak and regenerating."
            $needWrite = $true
        }
    } else {
        $needWrite = $true
    }

    # Merge any new agent IPs (dedupe by Address).
    $addrs  = @($existingAgents | ForEach-Object { $_.Address })
    $merged = New-Object System.Collections.ArrayList
    foreach ($a in $existingAgents) { [void]$merged.Add($a) }
    $n = $merged.Count
    foreach ($ip in $AgentIPs) {
        $addr = "http://${ip}:$AgentPort"
        if ($addrs -notcontains $addr) { $n++; [void]$merged.Add(@{ Name = "Agent$n"; Address = $addr }); $needWrite = $true }
    }
    if ($merged.Count -eq 0) { [void]$merged.Add(@{ Name = 'Agent1'; Address = "http://AGENT_IP_HERE:$AgentPort" }) }

    if ($needWrite) {
        Write-ControllerAppSettings -Path $appSettings -Port $port -VocabFile $vocabFile -Agents $merged
        Write-Ok "Wrote appsettings.json ($($merged.Count) agent entries)"
        if ($AgentIPs.Count -eq 0 -and $existingAgents.Count -eq 0) { Add-Warn "Edit $appSettings to set real agent IPs (placeholder written)." }
    } else {
        Write-Note 'appsettings.json already consistent (kept)'
    }

    if (-not (Test-Path $vocabFile)) { New-StarterWatchList -Path $vocabFile; Write-Ok "Created starter WatchList.xml" }
    else { Write-Note 'WatchList.xml exists (kept)' }

    # Let the run-as account write logs / vocabulary when the controller runs as wwApps.
    foreach ($d in @($InstallDir, $VocabularyDir, (Join-Path $VocabularyDir 'Logs'))) {
        try { & icacls "$d" /grant "${LogonUser}:(OI)(CI)M" /T /C *> $null } catch { }
    }
    Write-Ok "Granted $LogonUser modify rights on the controller directories"
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
    Write-Ok 'UAC disabled (EnableLUA=0, silent elevation). Reboot required to fully take effect.'
    $script:RebootNeeded = $true
}

# ============================================================================
#  PHASE 7 - trust controller/agent URLs + kill exe-launch security prompts
# ============================================================================
function Disable-SecurityPrompts {
    # 7a. Add controller + agent hosts to the Local Intranet zone (ZoneMapKey, "1" = Intranet).
    $zoneHosts = New-Object System.Collections.Generic.List[string]
    foreach ($h in @($env:COMPUTERNAME, 'localhost', '127.0.0.1', $ControllerHost)) { if ($h) { $zoneHosts.Add($h) | Out-Null } }
    try {
        $fqdn = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
        if ($fqdn) { $zoneHosts.Add($fqdn) | Out-Null }
    } catch { }
    foreach ($ip in $AgentIPs)          { if ($ip) { $zoneHosts.Add($ip) | Out-Null } }
    foreach ($h in $ExtraTrustedHosts)  { if ($h)  { $zoneHosts.Add($h)  | Out-Null } }
    $zoneHosts = $zoneHosts | Select-Object -Unique

    foreach ($base in @(
            'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMapKey',
            'HKCU:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMapKey')) {
        if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }
        foreach ($h in $zoneHosts) {
            foreach ($scheme in @('http', 'https')) {
                New-ItemProperty -Path $base -Name "${scheme}://${h}" -Value '1' -PropertyType String -Force | Out-Null
            }
        }
    }
    Write-Ok ("Added to Local Intranet zone: {0}" -f ($zoneHosts -join ', '))

    # 7b. Attachment Manager: stop the "Open File - Security Warning" globally (HKLM + HKCU).
    foreach ($hive in @('HKLM', 'HKCU')) {
        $att = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"
        Set-RegValue $att 'SaveZoneInformation'      1            # 1 = do NOT attach Mark-of-the-Web to saved files
        Set-RegValue $att 'HideZoneInfoOnProperties' 1
        Set-RegValue $att 'ScanWithAntiVirus'        1            # 1 = off

        $assoc = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Associations"
        Set-RegValue $assoc 'DefaultFileTypeRisk' 0x1808          # 0x1808 = Low risk (no prompt)
        Set-RegValue $assoc 'LowRiskFileTypes' '.exe;.bat;.cmd;.ps1;.psm1;.msi;.vbs;.reg;.dll;.com;.zip;.7z;' 'String'

        foreach ($z in 0, 1, 2, 3) {
            $zk = "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\$z"
            Set-RegValue $zk '1806' 0   # Launching applications and unsafe files -> Enable
            Set-RegValue $zk '1807' 0
            Set-RegValue $zk '2001' 0   # .NET: run signed components
            Set-RegValue $zk '2004' 0   # .NET: run unsigned components
        }
    }
    [Environment]::SetEnvironmentVariable('SEE_MASK_NOZONECHECKS', '1', 'Machine')
    $env:SEE_MASK_NOZONECHECKS = '1'
    Write-Ok 'Attachment-Manager prompts disabled (machine + user); SEE_MASK_NOZONECHECKS set.'

    # 7c. Strip Mark-of-the-Web from anything already deployed so it runs silently now.
    if (Test-Path $InstallDir) {
        Get-ChildItem -Path $InstallDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
        Write-Ok "Cleared Mark-of-the-Web under $InstallDir"
    }

    # 7d. Turn off IE Enhanced Security Configuration (Server SKUs; harmless elsewhere).
    try {
        $escAdmin = 'HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A509B1A7-37EF-4b3f-8CFC-4F3A74704073}'
        $escUser  = 'HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A509B1A8-37EF-4b3f-8CFC-4F3A74704073}'
        if (Test-Path $escAdmin) { Set-RegValue $escAdmin 'IsInstalled' 0 }
        if (Test-Path $escUser)  { Set-RegValue $escUser  'IsInstalled' 0 }
        Write-Note 'IE Enhanced Security Configuration disabled (if present).'
    } catch { Write-Note "IE ESC step skipped ($($_.Exception.Message))" }
}

# ============================================================================
#  Validation + banner + summary
# ============================================================================
function Invoke-Validation {
    Write-Host ''
    Write-Host '[*] Validation' -ForegroundColor Cyan

    $portInUse = Get-NetTCPConnection -LocalPort $ControllerPort -State Listen -ErrorAction SilentlyContinue
    if ($portInUse) {
        $procName = (Get-Process -Id $portInUse[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Write-Note "Controller port $ControllerPort already LISTENING (by $procName) - the controller may already be running."
    } else {
        Write-Ok "Controller port $ControllerPort is free"
    }

    $appcmd = Join-Path $env:windir 'system32\inetsrv\appcmd.exe'
    if (Test-Path $appcmd) { Write-Ok 'IIS appcmd present (deploy script ready)' } elseif (-not $SkipIIS) { Add-Warn 'IIS appcmd missing.' }

    if ($UseHostingBundle -and -not $SkipIIS) {
        if (Test-Path $script:AncmDll) { Write-Ok 'ASP.NET Core IIS module (ANCM) present' } else { Add-Warn 'ANCM missing (IIS hosting will fail).' }
    }

    $x64 = $DotNetInstallDir
    if ((Test-SharedFx $x64 'Microsoft.AspNetCore.App' $script:DotNetMajor) -and (Test-SharedFx $x64 'Microsoft.WindowsDesktop.App' $script:DotNetMajor)) {
        Write-Ok ".NET $($script:DotNetMajor) ASP.NET Core + Windows Desktop present (x64)"
    } elseif (-not $SkipDotNet) {
        Add-Warn ".NET $($script:DotNetMajor) runtimes not fully present (x64)."
    }

    $fw = Get-NetFirewallRule -DisplayName 'TestController*' -ErrorAction SilentlyContinue
    if ($fw) { Write-Ok ("Firewall rules active: {0}" -f @($fw).Count) }

    $script:LocalIP = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.PrefixOrigin -in 'Dhcp','Manual' -and $_.IPAddress -notlike '169.*' } |
        Select-Object -First 1).IPAddress
    if ($script:LocalIP) { Write-Note "Controller IP: $($script:LocalIP)  ->  give agents  http://$($script:LocalIP):$ControllerPort" }
}

function Write-Banner {
    Write-Host ''
    Write-Host '  ============================================================' -ForegroundColor Green
    Write-Host '   TestAgent CONTROLLER NODE PREP  (IIS + WPF gRPC, self-healing)' -ForegroundColor Green
    Write-Host ('   Host: {0}  Port: {1}  .NET {2}  Run-as: {3}' -f $env:COMPUTERNAME, $ControllerPort, $script:DotNetMajor, $LogonUser) -ForegroundColor Green
    Write-Host '  ============================================================' -ForegroundColor Green
}

function Write-Summary {
    Write-Host ''
    Write-Host '============================================================' -ForegroundColor Green
    Write-Host ' CONTROLLER PREP COMPLETE' -ForegroundColor Green
    Write-Host '============================================================' -ForegroundColor Green
    Write-Host (' Install dir   : {0}' -f $InstallDir)
    Write-Host (' Vocabulary    : {0}' -f $VocabularyDir)
    Write-Host (' Controller    : port {0} (agents connect here)' -f $ControllerPort)
    Write-Host (' Run-as        : {0}  (local admin + service/batch logon rights)' -f $LogonUser)
    Write-Host (' .NET 10       : {0}  ({1}{2})' -f ($DotNetArchitectures -join ' + '), `
                 $(if ($UseHostingBundle) { 'ASP.NET Hosting Bundle + ANCM' } else { 'standalone ASP.NET (no IIS module)' }), `
                 $(if ($IncludeNetCore) { ', with .NET Core' } else { '' }))
    Write-Host (' IIS           : {0}' -f $(if ($SkipIIS) { 'skipped' } else { 'enabled (appcmd, WebSockets for SignalR)' }))
    Write-Host ''
    if ($script:Warnings.Count) {
        Write-Host (' {0} warning(s):' -f $script:Warnings.Count) -ForegroundColor Yellow
        foreach ($w in $script:Warnings) { Write-Host "   - $w" -ForegroundColor Yellow }
    } else {
        Write-Host ' No warnings.' -ForegroundColor Green
    }
    Write-Host ''
    Write-Host ' NEXT:' -ForegroundColor Yellow
    Write-Host ("   1. Copy the published controller binaries to {0}" -f $InstallDir)
    Write-Host '   2. Deploy the WebApi to IIS with Deploy-TestController.ps1 (uses appcmd).'
    Write-Host ("   3. To run the WebApi app pool as {0}, re-run this with:" -f $LogonUser)
    Write-Host '        -ConfigureAppPoolIdentity -AppPoolName <yourPool>'
    Write-Host ("   4. Start the controller: {0}  (as Administrator or as {1})" -f $ControllerExePath, $LogonUser)
    if ($script:LocalIP) { Write-Host ("   5. Point agents at  http://{0}:{1}  (their ControllerAddress)." -f $script:LocalIP, $ControllerPort) }
    Write-Host ''
}

# ============================================================================
#  MAIN
# ============================================================================
Write-Banner

Write-Phase 'IIS + required features + appcmd (WebSockets for SignalR)'
if ($SkipIIS) { Write-Note 'Skipped (-SkipIIS)' }
else { try { Enable-IISFeatures } catch { Add-Warn "IIS phase: $($_.Exception.Message)" } }

Write-Phase 'Installing / repairing .NET 10 runtimes (x64 + x86; Hosting Bundle for IIS, one shot)'
if ($SkipDotNet) { Write-Note 'Skipped (-SkipDotNet)' }
else { try { Ensure-DotNetRuntime } catch { Add-Warn "DotNet phase: $($_.Exception.Message)" } }

Write-Phase 'Firewall (controller in / agents out / per-agent)'
if ($SkipFirewall) { Write-Note 'Skipped (-SkipFirewall)' }
else { try { Set-Networking } catch { Add-Warn "Networking phase: $($_.Exception.Message)" } }

Write-Phase 'Run-as account (wwApps -> local admin + logon rights)'
if ($SkipRunAsAccount) { Write-Note 'Skipped (-SkipRunAsAccount)' }
else { try { Set-RunAsAccount } catch { Add-Warn "Run-as phase: $($_.Exception.Message)" } }

Write-Phase 'Install dir + appsettings.json + WatchList.xml'
try { Ensure-ControllerConfig } catch { Add-Warn "Config phase: $($_.Exception.Message)" }

Write-Phase 'Disabling UAC'
if ($SkipUacDisable) { Write-Note 'Skipped (-SkipUacDisable)' }
else { try { Disable-UAC } catch { Add-Warn "UAC phase: $($_.Exception.Message)" } }

Write-Phase 'Trusting controller/agent URLs + suppressing Open-File security prompts'
if ($SkipSecurityPrompts) { Write-Note 'Skipped (-SkipSecurityPrompts)' }
else { try { Disable-SecurityPrompts } catch { Add-Warn "Security-prompt phase: $($_.Exception.Message)" } }

try { Invoke-Validation } catch { Add-Warn "Validation: $($_.Exception.Message)" }
Write-Summary

if ($script:RebootNeeded) {
    if ($RebootWhenDone) {
        Write-Host 'Rebooting in 10 seconds (Ctrl+C to cancel)...' -ForegroundColor Yellow
        Start-Sleep -Seconds 10
        try { Stop-Transcript | Out-Null } catch { }
        Restart-Computer -Force
    } else {
        $ans = Read-Host 'A reboot is recommended (UAC-off / IIS). Reboot now? [y/N]'
        if ($ans -match '^(y|yes)$') { try { Stop-Transcript | Out-Null } catch { }; Restart-Computer -Force }
        else { Write-Host 'Reboot later to finish activating UAC-off and IIS.' -ForegroundColor Yellow }
    }
}

try { Stop-Transcript | Out-Null } catch { }
exit $(if ($script:Warnings.Count) { 2 } else { 0 })
