<#
.SYNOPSIS
    Diagnoses the whole Azure DevOps credential chain for the TestController web tier.

.DESCRIPTION
    Answers, in one run, every question that has previously been guessed at when Code Churn
    shows no data. Run it BEFORE rotating a PAT or redeploying - it usually names the fix.

    The PAT value is NEVER printed. Only its length and a SHA256 fingerprint prefix are shown,
    which is enough to tell two tokens apart and to prove which scope holds which value.

    Checks performed:
      1. Credential presence and scope (Machine vs User) - w3wp can only see Machine.
      2. Whether the token actually authenticates against the endpoints the app really calls.
      3. Whether w3wp predates the credential, i.e. it is running on a stale environment block.
      4. What the application itself reports via /api/impact/health.
      5. The Ado:ForwardImpactToController topology trap - when forwarding is ON, Code Churn is
         served by the WPF controller and a green /api/impact/health means nothing.
      6. How many branches the branch picker should end up with.

.EXAMPLE
    .\Test-AdoCredential.ps1
    Diagnoses JVGR22 remotely using the caller's credentials.

.EXAMPLE
    .\Test-AdoCredential.ps1 -ComputerName localhost
    Runs directly on the server (use this over RDP).
#>
[CmdletBinding()]
param(
    [string] $ComputerName = 'JVGR22',
    [int]    $SitePort     = 81,
    [string] $Organization = 'AVEVA-VSTS',
    [string] $OmiProject   = 'AppServer OMI',
    [string] $WebRoot      = 'C:\inetpub\TestControllerWeb'
)

$ErrorActionPreference = 'Continue'

$probe = {
    param($SitePort, $Organization, $OmiProject, $WebRoot)

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    function Get-TokenFingerprint {
        param($Value)
        if ([string]::IsNullOrEmpty($Value)) { return 'ABSENT' }
        $sha = [Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))
        return (($hash[0..5] | ForEach-Object { $_.ToString('x2') }) -join '')
    }

    function Invoke-AdoProbe {
        param($Url, $Pat)
        $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$Pat"))
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri $Url -Headers @{ Authorization = "Basic $basic" } `
                                   -TimeoutSec 45 -ErrorAction Stop
            return @{ Status = [int]$r.StatusCode; Content = $r.Content }
        } catch {
            if ($_.Exception.Response) { return @{ Status = [int]$_.Exception.Response.StatusCode; Content = $null } }
            return @{ Status = -1; Content = $null; Error = $_.Exception.Message }
        }
    }

    function Invoke-LocalApi {
        param($Path)
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$SitePort$Path" -TimeoutSec 120 -ErrorAction Stop
            return @{ Status = [int]$r.StatusCode; Content = $r.Content }
        } catch {
            if ($_.Exception.Response) { return @{ Status = [int]$_.Exception.Response.StatusCode; Content = $null } }
            return @{ Status = -1; Content = $null; Error = $_.Exception.Message }
        }
    }

    $out = [ordered]@{}

    # ---- 1. credential presence and scope ----
    $machine = [Environment]::GetEnvironmentVariable('ADO_PAT', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('ADO_PAT', 'User')
    $out.MachinePresent = -not [string]::IsNullOrEmpty($machine)
    $out.UserPresent    = -not [string]::IsNullOrEmpty($user)
    $out.MachineLength  = if ($machine) { $machine.Length } else { 0 }
    $out.MachineFp      = Get-TokenFingerprint $machine
    $out.UserFp         = Get-TokenFingerprint $user
    $out.ScopesDiffer   = ($out.MachineFp -ne $out.UserFp)
    if ($machine) {
        $out.MachineHasWhitespace = ($machine -ne $machine.Trim())
        $out.MachineHasQuotes     = ($machine.StartsWith('"') -or $machine.EndsWith('"'))
        $out.MachineIsAscii       = -not @($machine.ToCharArray() | Where-Object { [int]$_ -gt 127 }).Count
    }

    # ---- 2. does the token actually authenticate ----
    if ($machine) {
        $org = [Uri]::EscapeDataString($Organization)
        $omi = [Uri]::EscapeDataString($OmiProject)
        $out.Probe_Projects   = (Invoke-AdoProbe "https://dev.azure.com/$org/_apis/projects?api-version=7.1" $machine).Status
        $out.Probe_OmiBuilds  = (Invoke-AdoProbe ('https://dev.azure.com/' + $org + '/' + $omi + '/_apis/build/builds?$top=1&api-version=7.1') $machine).Status
        # Informational only: a VALID PAT without the Profile scope returns 401 here. Never use it as the verdict.
        $out.Probe_ProfilesMe = (Invoke-AdoProbe 'https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1' $machine).Status
    }

    # ---- 3. process staleness ----
    $w3 = @(Get-Process -Name w3wp -ErrorAction SilentlyContinue)
    $out.W3wpCount = $w3.Count
    $out.W3wpStart = if ($w3.Count -gt 0) { ($w3 | ForEach-Object { $_.StartTime.ToString('s') }) -join ', ' } else { 'not running' }

    $ctrl = @(Get-Process -Name TestControllerGrpc -ErrorAction SilentlyContinue)
    $out.ControllerCount = $ctrl.Count
    $out.ControllerStart = if ($ctrl.Count -gt 0) { ($ctrl | ForEach-Object { $_.StartTime.ToString('s') }) -join ', ' } else { 'not running' }
    foreach ($port in @(5100, 5200)) {
        $t = Test-NetConnection -ComputerName localhost -Port $port -WarningAction SilentlyContinue
        $out["Port$port"] = [bool]$t.TcpTestSucceeded
    }

    # ---- 4. what the app itself reports ----
    $health = Invoke-LocalApi '/api/impact/health'
    $out.HealthStatus = $health.Status
    $out.HealthBody   = $health.Content

    # ---- 5. forwarding topology ----
    $cfgPath = Join-Path $WebRoot 'appsettings.json'
    if (Test-Path $cfgPath) {
        try {
            $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
            $out.ConfigAuthMode   = [string]$cfg.Ado.AuthMode
            $out.ConfigEnabled    = [bool]$cfg.Ado.Enabled
            $out.ConfigForwarding = [bool]$cfg.Ado.ForwardImpactToController
            $patterns = @($cfg.Ado.BranchIncludePatterns)
            if ($patterns.Count -eq 0 -or -not $patterns[0]) { $patterns = @('releases/*', 'release/*', 'prod/*') }
            $out.BranchPatterns = $patterns
        } catch {
            $out.ConfigError = $_.Exception.Message
        }
    } else {
        $out.ConfigError = "appsettings.json not found at $cfgPath"
    }

    if ($out.ConfigForwarding -and $out.Port5200) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:5200/api/impact/health' -TimeoutSec 60 -ErrorAction Stop
            $out.ControllerHealthBody = $r.Content
        } catch {
            $out.ControllerHealthBody = "unreachable: $($_.Exception.Message)"
        }
    }

    # ---- 6. branch projection ----
    if ($machine -and $out.Probe_OmiBuilds -eq 200) {
        $org = [Uri]::EscapeDataString($Organization)
        $omi = [Uri]::EscapeDataString($OmiProject)
        $res = Invoke-AdoProbe ('https://dev.azure.com/' + $org + '/' + $omi + '/_apis/build/builds?$top=200&queryOrder=finishTimeDescending&api-version=7.1') $machine
        if ($res.Status -eq 200 -and $res.Content) {
            $data = $res.Content | ConvertFrom-Json
            $branches = @(@($data.value) |
                ForEach-Object { $_.sourceBranch } |
                Where-Object { $_ } |
                ForEach-Object { $_ -replace '^refs/heads/', '' } |
                Select-Object -Unique)
            $out.DistinctBranches = $branches.Count
            $pats = @($out.BranchPatterns)
            $matched = @($branches | Where-Object { $b = $_; @($pats | Where-Object { $b -like $_ }).Count -gt 0 })
            $out.MatchingBranches = $matched.Count
            $out.MatchingBranchNames = $matched
        }
    }

    [pscustomobject]$out
}

# --- execute locally or remotely -------------------------------------------------
$isLocal = ($ComputerName -in @('localhost', '.', $env:COMPUTERNAME))
if ($isLocal) {
    $r = & $probe $SitePort $Organization $OmiProject $WebRoot
} else {
    $r = Invoke-Command -ComputerName $ComputerName -ScriptBlock $probe `
                        -ArgumentList $SitePort, $Organization, $OmiProject, $WebRoot
}

# --- report ----------------------------------------------------------------------
function Write-Check {
    param($Label, $Ok, $Detail, [switch]$WarnOnly)
    $mark = if ($Ok) { '[ PASS ]' } elseif ($WarnOnly) { '[ WARN ]' } else { '[ FAIL ]' }
    $color = if ($Ok) { 'Green' } elseif ($WarnOnly) { 'Yellow' } else { 'Red' }
    Write-Host $mark -ForegroundColor $color -NoNewline
    Write-Host (' {0,-38} {1}' -f $Label, $Detail)
}

$problems = New-Object System.Collections.Generic.List[string]

Write-Host ''
Write-Host "ADO CREDENTIAL DIAGNOSTIC - $ComputerName" -ForegroundColor Cyan
Write-Host ('=' * 78)

Write-Host ''
Write-Host '1. CREDENTIAL PRESENCE AND SCOPE' -ForegroundColor Cyan
Write-Check 'ADO_PAT at Machine scope' $r.MachinePresent "fp=$($r.MachineFp) len=$($r.MachineLength)"
if (-not $r.MachinePresent) {
    $problems.Add('ADO_PAT is not set at Machine scope. The IIS app pool runs as IIS AppPool\TestControllerWeb and reads only the Machine block.')
}
if ($r.UserPresent) {
    Write-Check 'ADO_PAT at User scope' $true "fp=$($r.UserFp)" -WarnOnly:$false
}
if ($r.MachinePresent -and $r.UserPresent -and $r.ScopesDiffer) {
    Write-Check 'Machine and User values match' $false 'DIFFERENT tokens in each scope' -WarnOnly
    $problems.Add('Machine and User scope hold DIFFERENT tokens. If you just rotated the PAT you probably set it at User scope, which the app can never see. Copy it to Machine scope, then run iisreset.')
}
if ($r.MachinePresent) {
    $clean = (-not $r.MachineHasWhitespace) -and (-not $r.MachineHasQuotes) -and $r.MachineIsAscii
    Write-Check 'Value is clean' $clean "whitespace=$($r.MachineHasWhitespace) quotes=$($r.MachineHasQuotes) ascii=$($r.MachineIsAscii)"
    if (-not $clean) { $problems.Add('The stored PAT has stray whitespace, quotes, or non-ASCII characters. Re-set it.') }
}

Write-Host ''
Write-Host '2. DOES THE TOKEN AUTHENTICATE' -ForegroundColor Cyan
if ($r.MachinePresent) {
    Write-Check 'GET _apis/projects' ($r.Probe_Projects -eq 200) "HTTP $($r.Probe_Projects)"
    Write-Check 'GET OMI build/builds' ($r.Probe_OmiBuilds -eq 200) "HTTP $($r.Probe_OmiBuilds)"
    Write-Host ('         {0,-38} HTTP {1}  (informational only - a valid PAT' -f 'GET profile/profiles/me', $r.Probe_ProfilesMe)
    Write-Host ('         {0,-38} without the Profile scope returns 401 here)' -f '')
    if ($r.Probe_Projects -ne 200 -or $r.Probe_OmiBuilds -ne 200) {
        if ($r.Probe_Projects -eq 401 -or $r.Probe_OmiBuilds -eq 401) {
            $problems.Add('HTTP 401: the token is rejected outright - expired, revoked, or for another organisation. Issue a new PAT.')
        } elseif ($r.Probe_Projects -eq 403 -or $r.Probe_OmiBuilds -eq 403) {
            $problems.Add('HTTP 403: the token authenticates but is not authorised - project permissions or an Entra Conditional Access policy. A new PAT will NOT fix this.')
        } else {
            $problems.Add("Unexpected status from ADO (projects=$($r.Probe_Projects), builds=$($r.Probe_OmiBuilds)).")
        }
    }
}

Write-Host ''
Write-Host '3. PROCESS STATE' -ForegroundColor Cyan
Write-Check 'w3wp running' ($r.W3wpCount -gt 0) "count=$($r.W3wpCount) started=$($r.W3wpStart)"
Write-Check 'WPF controller running' ($r.ControllerCount -gt 0) "started=$($r.ControllerStart)" -WarnOnly:(-not $r.ConfigForwarding)
Write-Check 'Port 5100 (agent gRPC)' ([bool]$r.Port5100) ''
Write-Check 'Port 5200 (controller API)' ([bool]$r.Port5200) ''
Write-Host '         NOTE: after changing a Machine env var you MUST run iisreset.'
Write-Host '               An app-pool recycle inherits the stale environment block from WAS.'

Write-Host ''
Write-Host '4. WHAT THE APPLICATION REPORTS' -ForegroundColor Cyan
Write-Check '/api/impact/health reachable' ($r.HealthStatus -eq 200) "HTTP $($r.HealthStatus)"
if ($r.HealthBody) {
    $h = $r.HealthBody | ConvertFrom-Json
    Write-Check '  adoReachable' ([bool]$h.adoReachable) "credentialSource=$($h.credentialSource) componentCount=$($h.componentCount)"
    if ($h.probeError) {
        # A policy block answers with a full HTML page; show only the first readable line.
        $pe = ($h.probeError -replace '\s+', ' ').Trim()
        if ($pe -match 'VS\d{6}[^<]*') { $pe = $Matches[0].Trim() }
        elseif ($pe.Length -gt 300) { $pe = $pe.Substring(0, 300) + '...' }
        Write-Host "         probeError: $pe" -ForegroundColor Yellow
        if ($h.probeError -match 'VS403463|conditional access') {
            $problems.Add('Entra Conditional Access is blocking this credential (VS403463). A PAT cannot satisfy a CA policy, so reissuing one will NOT help. Use interactive sign-in on the controller (set Ado:ForwardImpactToController=true), switch to ServicePrincipal auth, or have an Entra admin exclude the identity.')
        } else {
            $problems.Add("The web tier cannot reach ADO: $pe")
        }
    }
    Write-Host '         NOTE: credentialConfigured only means the env var is non-empty.'
    Write-Host '               It reports true for a dead token. Trust adoReachable instead.'
}

Write-Host ''
Write-Host '5. TOPOLOGY (which host actually serves Code Churn)' -ForegroundColor Cyan
Write-Host ('         Ado:AuthMode                 = {0}' -f $r.ConfigAuthMode)
Write-Host ('         Ado:ForwardImpactToController = {0}' -f $r.ConfigForwarding)
if ($r.ConfigForwarding) {
    Write-Host '         Code Churn is served by the WPF CONTROLLER, not by this PAT.' -ForegroundColor Yellow
    Write-Host '         /api/impact/health describes the LOCAL host only, so it can read' -ForegroundColor Yellow
    Write-Host '         green while every Code Churn call fails.' -ForegroundColor Yellow
    if ($r.ControllerHealthBody) {
        Write-Host "         controller health: $($r.ControllerHealthBody)"
        if ($r.ControllerHealthBody -match 'Not signed in') {
            $problems.Add('Forwarding is ON and the controller is NOT signed in to ADO. Either sign in on the WPF CodeChurn tab, or set Ado:ForwardImpactToController=false to use this host PAT.')
        }
    }
    if ($r.ControllerCount -eq 0) {
        $problems.Add('Forwarding is ON but the WPF controller is not running, so every /api/impact/* call returns 502. Start TestControllerGrpc.exe on the server.')
    }
} else {
    Write-Host '         Code Churn is served LOCALLY using the PAT above.' -ForegroundColor Green
}

Write-Host ''
Write-Host '6. BRANCH PICKER PROJECTION' -ForegroundColor Cyan
if ($null -ne $r.DistinctBranches) {
    Write-Host ('         Include patterns   : {0}' -f (@($r.BranchPatterns) -join ', '))
    Write-Host ('         Distinct branches  : {0}' -f $r.DistinctBranches)
    Write-Check '  Branches after filter' ($r.MatchingBranches -gt 0) "$($r.MatchingBranches) expected in the picker"
    foreach ($b in @($r.MatchingBranchNames)) { Write-Host "             - $b" }
    if ($r.MatchingBranches -eq 0) {
        $problems.Add('No recent build branch matches Ado:BranchIncludePatterns, so the picker will be empty even though ADO auth works.')
    }
} else {
    Write-Host '         Skipped (no usable credential).'
}

Write-Host ''
Write-Host ('=' * 78)
if ($problems.Count -eq 0) {
    Write-Host 'VERDICT: healthy - no credential or topology problem detected.' -ForegroundColor Green
} else {
    Write-Host "VERDICT: $($problems.Count) problem(s) found" -ForegroundColor Red
    $i = 1
    foreach ($p in $problems) {
        Write-Host ""
        Write-Host "  $i. $p" -ForegroundColor Yellow
        $i++
    }
}
Write-Host ''
