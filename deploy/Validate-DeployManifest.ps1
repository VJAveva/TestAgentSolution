<#
.SYNOPSIS
    Validates a deployment manifest and its artifact checksums.

.DESCRIPTION
    Reads deploy-manifest.json and verifies:
    - All required fields are present (gitSha, protoHash, checksums)
    - Artifact directories exist and contain the expected executables
    - SHA256 checksums of executables match what was recorded at build time
    - Proto hash matches the current source (detects stale artifacts)

.PARAMETER ManifestPath
    Path to the deploy-manifest.json file. Default: C:\Deployment\deploy-manifest.json

.PARAMETER SolutionRoot
    Path to the solution root (for proto hash comparison). Default: script location.

.PARAMETER SkipProtoCheck
    Skip proto hash comparison against current source.

.EXAMPLE
    .\Validate-DeployManifest.ps1
    .\Validate-DeployManifest.ps1 -ManifestPath "D:\Staging\deploy-manifest.json"
#>

[CmdletBinding()]
param(
    [string]$ManifestPath = "C:\Deployment\deploy-manifest.json",
    [string]$SolutionRoot = "",
    [switch]$SkipProtoCheck
)

$ErrorActionPreference = "Stop"

# -- Resolve paths --
if (-not $SolutionRoot) {
    $SolutionRoot = $PSScriptRoot
    if (-not $SolutionRoot) { $SolutionRoot = (Get-Location).Path }
}

function Write-Status {
    param([string]$Message, [string]$Status = "INFO")
    $color = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "WARN" { "Yellow" }
        default { "White" }
    }
    Write-Host "  [$Status] $Message" -ForegroundColor $color
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host "    Deployment Manifest Validation" -ForegroundColor Cyan
Write-Host "  ============================================================" -ForegroundColor Cyan
Write-Host ""

# -- Load manifest --
if (-not (Test-Path $ManifestPath)) {
    Write-Status "Manifest not found: $ManifestPath" "FAIL"
    exit 1
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
Write-Status "Manifest loaded: $ManifestPath" "PASS"

$errors = @()
$warnings = @()

# -- Validate required fields --
Write-Host ""
Write-Host "  Metadata:" -ForegroundColor DarkGray

$requiredFields = @("generatedAt", "gitSha", "protoHash", "configuration", "runtime")
foreach ($field in $requiredFields) {
    $value = $manifest.$field
    if ([string]::IsNullOrWhiteSpace($value) -or $value -eq "unknown") {
        if ($field -eq "gitSha" -and $value -eq "unknown") {
            $warnings += "Git SHA is 'unknown' - build may not be from a git repository."
            Write-Status "$field`: $value" "WARN"
        } else {
            $errors += "Required field '$field' is missing or empty."
            Write-Status "$field`: MISSING" "FAIL"
        }
    } else {
        Write-Status "$field`: $value" "PASS"
    }
}

# Display additional info
if ($manifest.buildMachine) { Write-Host "    Build machine: $($manifest.buildMachine)" -ForegroundColor DarkGray }
if ($manifest.agentCapVersion) { Write-Host "    Agent capability version: $($manifest.agentCapVersion)" -ForegroundColor DarkGray }

# -- Validate proto hash against current source --
Write-Host ""
Write-Host "  Proto Compatibility:" -ForegroundColor DarkGray

if (-not $SkipProtoCheck) {
    $protoFile = Join-Path $SolutionRoot "TestAgentGrpc\Protos\test_agent.proto"
    if (Test-Path $protoFile) {
        $currentProtoHash = (Get-FileHash $protoFile -Algorithm SHA256).Hash.Substring(0, 16)
        if ($manifest.protoHash -eq $currentProtoHash) {
            Write-Status "Proto hash matches current source" "PASS"
        } else {
            $errors += "Proto hash mismatch! Manifest: $($manifest.protoHash), Current: $currentProtoHash. Artifacts may be incompatible."
            Write-Status "Proto hash MISMATCH (manifest: $($manifest.protoHash), current: $currentProtoHash)" "FAIL"
        }
    } else {
        $warnings += "Proto file not found at $protoFile - cannot verify compatibility."
        Write-Status "Proto file not found - skipping" "WARN"
    }
} else {
    Write-Status "Proto check skipped" "WARN"
}

# -- Validate project artifacts --
Write-Host ""
Write-Host "  Artifact Integrity:" -ForegroundColor DarkGray

if ($manifest.projects) {
    foreach ($proj in $manifest.projects) {
        $projPath = $proj.path
        $projExe = $proj.executable

        if (-not (Test-Path $projPath)) {
            $errors += "Project '$($proj.name)' artifact directory missing: $projPath"
            Write-Status "$($proj.name): directory MISSING" "FAIL"
            continue
        }

        $exePath = Join-Path $projPath $projExe
        if (-not (Test-Path $exePath)) {
            $errors += "Project '$($proj.name)' executable missing: $exePath"
            Write-Status "$($proj.name): executable MISSING ($projExe)" "FAIL"
            continue
        }

        # Verify checksum if available
        $expectedHash = $null
        if ($manifest.checksums -and $manifest.checksums.PSObject.Properties[$proj.name]) {
            $expectedHash = $manifest.checksums.($proj.name)
        }

        if ($expectedHash) {
            $actualHash = (Get-FileHash $exePath -Algorithm SHA256).Hash
            if ($actualHash -eq $expectedHash) {
                Write-Status "$($proj.name): checksum verified ($($proj.files) files, $($proj.sizeMB) MB)" "PASS"
            } else {
                $errors += "Project '$($proj.name)' checksum mismatch! File may be corrupted or tampered."
                Write-Status "$($proj.name): checksum MISMATCH" "FAIL"
            }
        } else {
            $warnings += "Project '$($proj.name)' has no checksum in manifest - integrity unverified."
            Write-Status "$($proj.name): present but no checksum ($($proj.files) files, $($proj.sizeMB) MB)" "WARN"
        }
    }
} else {
    $errors += "No projects listed in manifest."
    Write-Status "No projects in manifest" "FAIL"
}

# -- Summary --
Write-Host ""
Write-Host "  ============================================================" -ForegroundColor DarkGray

if ($errors.Count -eq 0 -and $warnings.Count -eq 0) {
    Write-Host "  VALIDATION PASSED - All artifacts verified." -ForegroundColor Green
    Write-Host "  ============================================================" -ForegroundColor Green
    exit 0
}

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "  Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($w in $warnings) {
        Write-Host "    - $w" -ForegroundColor Yellow
    }
}

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "  Errors ($($errors.Count)):" -ForegroundColor Red
    foreach ($e in $errors) {
        Write-Host "    - $e" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "  VALIDATION FAILED - Do NOT deploy these artifacts." -ForegroundColor Red
    Write-Host "  ============================================================" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  VALIDATION PASSED with warnings." -ForegroundColor Yellow
Write-Host "  ============================================================" -ForegroundColor Yellow
exit 0
