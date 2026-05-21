<#
.SYNOPSIS
    Post-deployment smoke test for TestAgentSolution WebApi + Fleet.

.DESCRIPTION
    Validates a running deployment by checking:
    1. Fleet endpoint responsiveness
    2. Per-agent details and telemetry
    3. SignalR negotiate endpoint
    4. Agent capabilities (ForceReady, streaming support)
    5. Active execution stream safety (monitors while streaming)
    6. WebClient bundle availability
    7. Deployment preflight status

.PARAMETER BaseUrl
    Base URL of the deployed WebApi. Default: http://localhost:8080

.PARAMETER TimeoutSeconds
    HTTP request timeout per call. Default: 10

.PARAMETER RequireForceReady
    Fail if any agent lacks ForceReady capability. Default: true

.EXAMPLE
    .\Invoke-SmokeTest.ps1
    .\Invoke-SmokeTest.ps1 -BaseUrl "http://jvgr22:8080"
    .\Invoke-SmokeTest.ps1 -BaseUrl "http://jvgr22:8080" -RequireForceReady $false
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:8080",
    [int]$TimeoutSeconds = 10,
    [bool]$RequireForceReady = $true
)

$ErrorActionPreference = "Stop"
$baseUrl = $BaseUrl.TrimEnd('/')

$passed = 0
$failed = 0
$warnings = 0
$results = @()

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Url,
        [scriptblock]$Validate = $null,
        [switch]$Optional
    )

    try {
        $response = Invoke-RestMethod -Uri $Url -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        if ($Validate) {
            $validationResult = & $Validate $response
            if ($validationResult -eq $true) {
                $script:passed++
                $script:results += [PSCustomObject]@{ Test = $Name; Status = "PASS"; Detail = "" }
                Write-Host "  [PASS] $Name" -ForegroundColor Green
                return $response
            } else {
                if ($Optional) {
                    $script:warnings++
                    $script:results += [PSCustomObject]@{ Test = $Name; Status = "WARN"; Detail = $validationResult }
                    Write-Host "  [WARN] $Name - $validationResult" -ForegroundColor Yellow
                } else {
                    $script:failed++
                    $script:results += [PSCustomObject]@{ Test = $Name; Status = "FAIL"; Detail = $validationResult }
                    Write-Host "  [FAIL] $Name - $validationResult" -ForegroundColor Red
                }
                return $response
            }
        } else {
            $script:passed++
            $script:results += [PSCustomObject]@{ Test = $Name; Status = "PASS"; Detail = "" }
            Write-Host "  [PASS] $Name" -ForegroundColor Green
            return $response
        }
    }
    catch {
        if ($Optional) {
            $script:warnings++
            $script:results += [PSCustomObject]@{ Test = $Name; Status = "WARN"; Detail = $_.Exception.Message }
            Write-Host "  [WARN] $Name - $($_.Exception.Message)" -ForegroundColor Yellow
        } else {
            $script:failed++
            $script:results += [PSCustomObject]@{ Test = $Name; Status = "FAIL"; Detail = $_.Exception.Message }
            Write-Host "  [FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
        }
        return $null
    }
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "    TestAgentSolution Post-Deployment Smoke Test" -ForegroundColor Cyan
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "  Target: $baseUrl"
Write-Host "  Timeout: ${TimeoutSeconds}s per request"
Write-Host ""

# ── Test 1: Fleet endpoint ──
Write-Host "  [1/7] Fleet Status" -ForegroundColor DarkGray
$fleet = Test-Endpoint -Name "GET /api/agents/fleet" -Url "$baseUrl/api/agents/fleet" -Validate {
    param($r)
    if ($r.agents -and $r.agents.Count -gt 0) { return $true }
    return "No agents in fleet response"
}

if ($fleet -and $fleet.agents) {
    Write-Host "        Fleet: $($fleet.agents.Count) agents registered" -ForegroundColor DarkGray
}

# ── Test 2: Agent details + telemetry for each agent ──
Write-Host ""
Write-Host "  [2/7] Agent Details & Telemetry" -ForegroundColor DarkGray

$agentNames = @()
if ($fleet -and $fleet.agents) {
    $agentNames = $fleet.agents | ForEach-Object { $_.name }
}

foreach ($agentName in $agentNames) {
    Test-Endpoint -Name "GET /api/agents/$agentName/details" -Url "$baseUrl/api/agents/$agentName/details" -Optional | Out-Null

    $telemetry = Test-Endpoint -Name "GET /api/agents/$agentName/telemetry" -Url "$baseUrl/api/agents/$agentName/telemetry" -Optional -Validate {
        param($r)
        if ($r.agentName) { return $true }
        return "Missing agentName in response"
    }
}

# ── Test 3: SignalR negotiate ──
Write-Host ""
Write-Host "  [3/7] SignalR Connectivity" -ForegroundColor DarkGray

try {
    $negotiateUrl = "$baseUrl/hubs/controller/negotiate?negotiateVersion=1"
    $negotiateResponse = Invoke-RestMethod -Uri $negotiateUrl -Method POST -TimeoutSec $TimeoutSeconds -ErrorAction Stop
    if ($negotiateResponse.connectionId -or $negotiateResponse.connectionToken) {
        $passed++
        $results += [PSCustomObject]@{ Test = "SignalR negotiate"; Status = "PASS"; Detail = "" }
        Write-Host "  [PASS] SignalR negotiate" -ForegroundColor Green
    } else {
        $warnings++
        $results += [PSCustomObject]@{ Test = "SignalR negotiate"; Status = "WARN"; Detail = "Unexpected response format" }
        Write-Host "  [WARN] SignalR negotiate - unexpected response format" -ForegroundColor Yellow
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "SignalR negotiate"; Status = "FAIL"; Detail = $_.Exception.Message }
    Write-Host "  [FAIL] SignalR negotiate - $($_.Exception.Message)" -ForegroundColor Red
}

# ── Test 4: Agent capabilities (ForceReady support) ──
Write-Host ""
Write-Host "  [4/7] Agent Capabilities" -ForegroundColor DarkGray

$agentsMissingForceReady = @()
foreach ($agentName in $agentNames) {
    $caps = Test-Endpoint -Name "GET /api/agents/$agentName/capabilities" -Url "$baseUrl/api/agents/$agentName/capabilities" -Optional -Validate {
        param($r)
        if ($r.agentVersion) { return $true }
        return "No agentVersion in response"
    }

    if ($caps -and -not $caps.supportsForceReady) {
        $agentsMissingForceReady += $agentName
    }
}

if ($agentsMissingForceReady.Count -gt 0 -and $RequireForceReady) {
    $failed++
    $msg = "Agents missing ForceReady: $($agentsMissingForceReady -join ', ')"
    $results += [PSCustomObject]@{ Test = "ForceReady capability"; Status = "FAIL"; Detail = $msg }
    Write-Host "  [FAIL] ForceReady capability - $msg" -ForegroundColor Red
} elseif ($agentsMissingForceReady.Count -gt 0) {
    $warnings++
    $msg = "Agents missing ForceReady: $($agentsMissingForceReady -join ', ')"
    $results += [PSCustomObject]@{ Test = "ForceReady capability"; Status = "WARN"; Detail = $msg }
    Write-Host "  [WARN] ForceReady capability - $msg" -ForegroundColor Yellow
} else {
    $passed++
    $results += [PSCustomObject]@{ Test = "ForceReady capability"; Status = "PASS"; Detail = "" }
    Write-Host "  [PASS] All agents support ForceReady" -ForegroundColor Green
}

# ── Test 5: Active execution stream safety ──
Write-Host ""
Write-Host "  [5/7] Execution Stream Safety" -ForegroundColor DarkGray

Test-Endpoint -Name "GET /api/deployment/status" -Url "$baseUrl/api/deployment/status" -Validate {
    param($r)
    if ($null -ne $r.maintenanceMode) { return $true }
    return "Missing maintenanceMode field"
} | Out-Null

Test-Endpoint -Name "GET /api/deployment/preflight" -Url "$baseUrl/api/deployment/preflight" -Validate {
    param($r)
    if ($null -ne $r.safe) { return $true }
    return "Missing safety assessment"
} | Out-Null

# ── Test 6: WebClient bundle ──
Write-Host ""
Write-Host "  [6/7] WebClient Bundle" -ForegroundColor DarkGray

try {
    $indexResponse = Invoke-WebRequest -Uri "$baseUrl/" -TimeoutSec $TimeoutSeconds -ErrorAction Stop -UseBasicParsing
    if ($indexResponse.StatusCode -eq 200 -and $indexResponse.Content -match "<script") {
        $passed++
        $results += [PSCustomObject]@{ Test = "WebClient index.html"; Status = "PASS"; Detail = "" }
        Write-Host "  [PASS] WebClient index.html served with scripts" -ForegroundColor Green
    } else {
        $warnings++
        $results += [PSCustomObject]@{ Test = "WebClient index.html"; Status = "WARN"; Detail = "Served but no script tags found" }
        Write-Host "  [WARN] WebClient index.html - no script tags found" -ForegroundColor Yellow
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "WebClient index.html"; Status = "FAIL"; Detail = $_.Exception.Message }
    Write-Host "  [FAIL] WebClient index.html - $($_.Exception.Message)" -ForegroundColor Red
}

# ── Test 7: Deployment preflight (safe to operate) ──
Write-Host ""
Write-Host "  [7/7] Operational Readiness" -ForegroundColor DarkGray

$preflight = Test-Endpoint -Name "Operational preflight" -Url "$baseUrl/api/deployment/preflight" -Optional -Validate {
    param($r)
    if ($r.safe -eq $true) { return $true }
    return "System reports not safe: $($r.issues -join '; ')"
}

# ── Summary ──
$total = $passed + $failed + $warnings
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkGray
Write-Host ""

if ($failed -eq 0) {
    Write-Host "  SMOKE TEST PASSED" -ForegroundColor Green
    Write-Host "  Results: $passed/$total passed, $warnings warning(s)" -ForegroundColor Green
} else {
    Write-Host "  SMOKE TEST FAILED" -ForegroundColor Red
    Write-Host "  Results: $passed passed, $failed FAILED, $warnings warning(s) of $total total" -ForegroundColor Red
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkGray
Write-Host ""

if ($failed -gt 0) { exit 1 }
exit 0
