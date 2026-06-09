<#
.SYNOPSIS
    Diagnose the gRPC "HTTP_1_1_REQUIRED" (0xd) condition on a TestAgent.

.DESCRIPTION
    Detects the case where the Agent's Kestrel endpoint serves HTTP/1.1 only,
    while the Controller's gRPC channel requires HTTP/2. Probes the endpoint with
    curl.exe (no .NET 5+ HttpVersionPolicy types) so it runs on Windows
    PowerShell 5.1.

    Exit codes (for batch orchestration):
      0  HTTP/2 confirmed - endpoint is gRPC-ready
      1  Could not test (curl missing, or port unreachable)
      3  HTTP_1_1_REQUIRED confirmed (server speaks HTTP/1.1 only)
      4  Neither HTTP/1.1 nor HTTP/2 responded

.NOTES
    Compatibility : Windows PowerShell 5.1 and PowerShell 7+ (ASCII-only)
    Version       : 3.0  |  May 2026
#>

[CmdletBinding()]
param(
    [string]$AgentHost       = "localhost",
    [int]   $AgentPort       = 5200,
    [string]$AgentInstallDir = "C:\TestAgentSolution\Agent"
)

$ErrorActionPreference = 'Continue'
$endpoint = "http://${AgentHost}:${AgentPort}/"

function Write-Section($t) {
    Write-Host ""
    Write-Host ("-" * 60) -ForegroundColor DarkGray
    Write-Host " $t"       -ForegroundColor Cyan
    Write-Host ("-" * 60) -ForegroundColor DarkGray
}
function Write-Pass($m) { Write-Host "  [PASS] $m" -ForegroundColor Green  }
function Write-Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red    }
function Write-Warn($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Write-Info($m) { Write-Host "  [INFO] $m" -ForegroundColor Gray   }

Write-Host ""
Write-Host "  TestAgent HTTP/2 Protocol Diagnostic" -ForegroundColor White
Write-Host "  Target : $endpoint"                   -ForegroundColor White
Write-Host "  Host   : $env:COMPUTERNAME"           -ForegroundColor White

# Locate curl.exe (ships with Windows 10 1803+ / Windows 11)
$curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
if (-not $curl) { $curl = Join-Path $env:SystemRoot 'System32\curl.exe' }
if (-not (Test-Path $curl)) {
    Write-Fail "curl.exe not found - Windows 10 1803+ or Windows 11 required."
    Write-Info "Cannot probe HTTP/2 without curl.exe."
    exit 1
}
Write-Info "Using curl: $curl"

# 1. TCP reachability --------------------------------------------------------
Write-Section "1. TCP Connectivity"
$reachable = $false
try {
    $reachable = Test-NetConnection -ComputerName $AgentHost -Port $AgentPort `
                                    -WarningAction SilentlyContinue -InformationLevel Quiet
} catch { Write-Warn "Test-NetConnection threw: $($_.Exception.Message)" }
if ($reachable) {
    Write-Pass "Port $AgentPort is reachable on $AgentHost"
} else {
    Write-Fail "Cannot reach ${AgentHost}:${AgentPort} (port closed or firewall blocking)"
    Write-Info "Fix reachability first, then re-run."
    exit 1
}

# 2. Local owning process (only if target is this machine) -------------------
$isLocal = @('localhost','127.0.0.1','::1',$env:COMPUTERNAME) -contains $AgentHost
if ($isLocal) {
    Write-Section "2. Local Process on Port $AgentPort"
    try {
        $conn = Get-NetTCPConnection -LocalPort $AgentPort -State Listen -ErrorAction Stop | Select-Object -First 1
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction Stop
        Write-Info "Process : $($proc.ProcessName) (PID $($proc.Id))"
        if ($proc.Path) { Write-Info "Path    : $($proc.Path)" }
        try {
            $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" -ErrorAction Stop).CommandLine
            if ($cmd) { Write-Info "Args    : $cmd" }
        } catch { }
    } catch {
        Write-Warn "Could not identify owning process (may require elevation): $($_.Exception.Message)"
    }
}

# 3. HTTP/1.1 sanity probe ---------------------------------------------------
Write-Section "3. HTTP/1.1 Probe (sanity check)"
$fmt = 'version=%{http_version};code=%{http_code};size=%{size_download}'
$out11  = & $curl --http1.1 -s -o NUL -w $fmt --max-time 5 $endpoint 2>&1
$exit11 = $LASTEXITCODE
$http11Ok = $false
if ($exit11 -eq 0) {
    Write-Info "Raw    : $out11"
    if ($out11 -match 'version=([\d.]+)') { Write-Info "Version: HTTP/$($matches[1])" }
    if ($out11 -match 'code=(\d+)')        { Write-Info "Status : $($matches[1])" }
    $http11Ok = $true
    Write-Pass "HTTP/1.1 succeeded - server process is alive"
} else {
    Write-Fail "HTTP/1.1 failed (curl exit $exit11): $out11"
}

# 4. HTTP/2 cleartext (h2c) probe - the critical test ------------------------
Write-Section "4. HTTP/2 Cleartext (h2c) Probe -- CRITICAL TEST"
$out2  = & $curl --http2-prior-knowledge -s -o NUL -w $fmt --max-time 5 $endpoint 2>&1
$exit2 = $LASTEXITCODE
$ver2  = $null
if ($out2 -match 'version=([\d.]+)') { $ver2 = $matches[1] }
$http2Ok = $false

if ($exit2 -eq 0 -and $ver2 -eq '2') {
    Write-Info "Raw    : $out2"
    Write-Pass "HTTP/2 succeeded - Kestrel speaks h2c on this endpoint"
    $http2Ok = $true
} elseif ($exit2 -eq 0 -and $ver2 -ne '2') {
    Write-Info "Raw    : $out2"
    Write-Fail "Server downgraded to HTTP/$ver2 - HTTP/2 NOT supported here"
} else {
    Write-Fail "HTTP/2 probe failed (curl exit $exit2)"
    Write-Info "Output : $out2"
    Write-Info "Notable curl exit codes: 16=HTTP/2 framing error (matches HTTP_1_1_REQUIRED), 56=reset, 92=stream error"
}

# 5. Diagnosis & remediation -------------------------------------------------
Write-Section "5. Diagnosis"

if ($http2Ok) {
    Write-Pass "Endpoint speaks HTTP/2. If gRPC still fails, look elsewhere:"
    Write-Info "  - proto / service contract mismatch between Controller and Agent"
    Write-Info "  - authentication / TLS misconfiguration"
    Write-Info "  - Controller pointing at the wrong agent address"
    Write-Host ""
    exit 0
}

if ($http11Ok) {
    Write-Host ""
    Write-Host "  +------------------------------------------------------+" -ForegroundColor Yellow
    Write-Host "  | CONFIRMED : HTTP_1_1_REQUIRED                         |" -ForegroundColor Yellow
    Write-Host ("  | Agent serves HTTP/1.1 only on port {0,-5}.            |" -f $AgentPort) -ForegroundColor Yellow
    Write-Host "  | Kestrel endpoint is NOT configured for HTTP/2.       |" -ForegroundColor Yellow
    Write-Host "  +------------------------------------------------------+" -ForegroundColor Yellow

    Write-Host ""
    Write-Host "  ROOT CAUSE" -ForegroundColor White
    Write-Host "    For plaintext http:// endpoints, Kestrel defaults to HTTP/1.1." -ForegroundColor Gray
    Write-Host "    gRPC requires HTTP/2. The endpoint must opt in to Http2."        -ForegroundColor Gray

    # Config inspection (local agent only)
    Write-Host ""
    Write-Host "  CONFIG INSPECTION" -ForegroundColor White
    if ($isLocal -and (Test-Path $AgentInstallDir)) {
        $cfgFiles = Get-ChildItem -Path $AgentInstallDir -Filter 'appsettings*.json' -ErrorAction SilentlyContinue
        if ($cfgFiles) {
            foreach ($f in $cfgFiles) {
                Write-Info "Found : $($f.FullName)"
                try {
                    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
                    $defaults = $null
                    if ($j.PSObject.Properties.Name -contains 'Kestrel' -and $j.Kestrel.EndpointDefaults) {
                        $defaults = $j.Kestrel.EndpointDefaults.Protocols
                    }
                    if ($defaults) {
                        Write-Info "  Kestrel.EndpointDefaults.Protocols = '$defaults'"
                        if ($defaults -notmatch 'Http2') { Write-Warn "  Does NOT include 'Http2' -- this is the bug." }
                    } else {
                        Write-Warn "  No Kestrel.EndpointDefaults.Protocols set (defaults to Http1)."
                    }
                    if ($j.PSObject.Properties.Name -contains 'Kestrel' -and $j.Kestrel.Endpoints) {
                        $j.Kestrel.Endpoints.PSObject.Properties | ForEach-Object {
                            Write-Info ("  Endpoint '{0}' Url={1} Protocols={2}" -f $_.Name, $_.Value.Url, $_.Value.Protocols)
                        }
                    }
                } catch { Write-Warn "  Could not parse $($f.Name): $($_.Exception.Message)" }
            }
        } else {
            Write-Info "No appsettings*.json found under $AgentInstallDir."
        }
    } else {
        Write-Info "Agent install dir not on this machine ($AgentInstallDir)."
        Write-Info "Re-run ON the agent host for full config inspection."
    }

    Write-Host ""
    Write-Host "  REMEDIATION -- Option A is preferred" -ForegroundColor White
    Write-Host ""
    Write-Host "  --- A. Fix the AGENT (Kestrel HTTP/2 opt-in) ---" -ForegroundColor Cyan
    Write-Host "  Setup-AgentNode.ps1 writes this to Agent\appsettings.json:" -ForegroundColor Gray
    Write-Host ''
    Write-Host '    "Kestrel": {'                                          -ForegroundColor White
    Write-Host '      "EndpointDefaults": { "Protocols": "Http2" }'        -ForegroundColor White
    Write-Host '    }'                                                     -ForegroundColor White
    Write-Host ''
    Write-Host "  If Program.cs binds the port in code, also set it there:" -ForegroundColor Gray
    Write-Host ''
    Write-Host '    builder.WebHost.ConfigureKestrel(o =>'                 -ForegroundColor White
    Write-Host ("        o.ListenAnyIP({0}, lo =>"          -f $AgentPort)  -ForegroundColor White
    Write-Host '            lo.Protocols = HttpProtocols.Http2));'         -ForegroundColor White
    Write-Host ''
    Write-Host "  Restart the Agent, then re-run this diagnostic."         -ForegroundColor Gray

    Write-Host ""
    Write-Host "  --- B. Switch to HTTPS (HTTP/2 via ALPN) ---"            -ForegroundColor Cyan
    Write-Host "  Cleanest for production; needs a cert on the agent."     -ForegroundColor Gray
    Write-Host "  Dev: dotnet dev-certs https --trust on the agent host."  -ForegroundColor Gray

    Write-Host ""
    Write-Host "  --- C. Controller-side switch (needed for h2c clients) ---" -ForegroundColor Cyan
    Write-Host "  Before the first GrpcChannel.ForAddress:"                -ForegroundColor Gray
    Write-Host ''
    Write-Host '    AppContext.SetSwitch('                                            -ForegroundColor White
    Write-Host '        "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport",'-ForegroundColor White
    Write-Host '        true);'                                                       -ForegroundColor White
    Write-Host ''
    Write-Host "  This alone will NOT fix it - the server must speak HTTP/2 too." -ForegroundColor Yellow
    Write-Host "  Apply A + C together when running plain HTTP."                    -ForegroundColor Yellow
    Write-Host ""
    exit 3
}

Write-Fail "Neither HTTP/1.1 nor HTTP/2 succeeded."
Write-Info "Possible causes:"
Write-Info "  - Agent process is not actually running on this port"
Write-Info "  - Endpoint expects TLS (try https://) and rejects plaintext"
Write-Info "  - A non-HTTP service is bound to the port"
Write-Host ""
exit 4
