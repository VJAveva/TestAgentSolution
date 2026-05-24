<#
.SYNOPSIS
    Generate self-signed TLS certificates for TestAgent/TestController gRPC communication.

.DESCRIPTION
    Creates a root CA certificate and server certificates for mTLS.
    Exports PFX files and installs into the LocalMachine\My store.

.PARAMETER OutputDir
    Directory to write PFX/CER files. Default: .\certs

.PARAMETER AgentHosts
    Comma-separated list of agent hostnames for SAN entries.

.PARAMETER ValidityDays
    Certificate validity period in days. Default: 365

.PARAMETER Password
    PFX export password. Default: prompted securely.

.EXAMPLE
    .\New-GrpcCertificates.ps1 -AgentHosts "AGENT01,AGENT02,AGENT03" -ValidityDays 730
#>
[CmdletBinding()]
param(
    [string]$OutputDir = ".\certs",
    [string]$AgentHosts = "localhost",
    [int]$ValidityDays = 365,
    [securestring]$Password
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Prompt for password if not provided
if (-not $Password) {
    $Password = Read-Host -Prompt "Enter PFX password" -AsSecureString
}

$notBefore = [DateTime]::UtcNow.AddDays(-1)
$notAfter  = [DateTime]::UtcNow.AddDays($ValidityDays)

Write-Host "=== TestAgent gRPC Certificate Generator ===" -ForegroundColor Cyan
Write-Host "Output:   $OutputDir"
Write-Host "Validity: $ValidityDays days ($($notBefore.ToString('yyyy-MM-dd')) to $($notAfter.ToString('yyyy-MM-dd')))"
Write-Host ""

# ── Step 1: Create Root CA ──────────────────────────────────────────────
Write-Host "[1/3] Creating Root CA certificate..." -ForegroundColor Yellow

$rootCa = New-SelfSignedCertificate `
    -Subject "CN=TestAgent Root CA" `
    -KeyAlgorithm RSA `
    -KeyLength 4096 `
    -KeyUsage CertSign, CRLSign, DigitalSignature `
    -KeyExportPolicy Exportable `
    -NotBefore $notBefore `
    -NotAfter $notAfter `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -TextExtension @("2.5.29.19={critical}{text}ca=TRUE")

$rootCaPath = Join-Path $OutputDir "TestAgent-RootCA.cer"
Export-Certificate -Cert $rootCa -FilePath $rootCaPath -Type CERT | Out-Null
Write-Host "  Root CA thumbprint: $($rootCa.Thumbprint)"
Write-Host "  Exported to: $rootCaPath"

# ── Step 2: Create Server Certificate (for Agent TLS listener) ──────────
Write-Host "[2/3] Creating Agent server certificate..." -ForegroundColor Yellow

$sanList = @($AgentHosts -split ',') | ForEach-Object { $_.Trim() }
$dnsEntries = ($sanList | ForEach-Object { "dns=$_" }) -join '&'

$serverCert = New-SelfSignedCertificate `
    -Subject "CN=TestAgent Server" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -KeyExportPolicy Exportable `
    -NotBefore $notBefore `
    -NotAfter $notAfter `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -Signer $rootCa `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1", "2.5.29.17={text}$dnsEntries")

$serverPfxPath = Join-Path $OutputDir "TestAgent-Server.pfx"
Export-PfxCertificate -Cert $serverCert -FilePath $serverPfxPath -Password $Password | Out-Null
Write-Host "  Server cert thumbprint: $($serverCert.Thumbprint)"
Write-Host "  SAN entries: $($sanList -join ', ')"
Write-Host "  Exported to: $serverPfxPath"

# ── Step 3: Create Client Certificate (for mTLS - Controller connecting to Agent) ──
Write-Host "[3/3] Creating Controller client certificate..." -ForegroundColor Yellow

$clientCert = New-SelfSignedCertificate `
    -Subject "CN=TestController Client" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -NotBefore $notBefore `
    -NotAfter $notAfter `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -Signer $rootCa `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.2")

$clientPfxPath = Join-Path $OutputDir "TestController-Client.pfx"
Export-PfxCertificate -Cert $clientCert -FilePath $clientPfxPath -Password $Password | Out-Null
Write-Host "  Client cert thumbprint: $($clientCert.Thumbprint)"
Write-Host "  Exported to: $clientPfxPath"

# ── Summary ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Certificate Generation Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Configuration values for appsettings.json:" -ForegroundColor Cyan
Write-Host ""
Write-Host "Agent (AgentKestrel section):"
Write-Host "  EnableTls: true"
Write-Host "  TlsPort: 5443"
Write-Host "  CertThumbprint: $($serverCert.Thumbprint)"
Write-Host "  OR CertFilePath: $serverPfxPath"
Write-Host "  RequireClientCertificate: true  (for mTLS)"
Write-Host ""
Write-Host "Controller (Security:Transport section):"
Write-Host "  GrpcMode: PlaintextAndTls  (or TlsOnly)"
Write-Host "  CertThumbprint: $($clientCert.Thumbprint)"
Write-Host "  TrustedCaThumbprint: $($rootCa.Thumbprint)"
Write-Host "  RequireMutualTls: true"
Write-Host ""
Write-Host "Root CA thumbprint (for trust): $($rootCa.Thumbprint)" -ForegroundColor Yellow
