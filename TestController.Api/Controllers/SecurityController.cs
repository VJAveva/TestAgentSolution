using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TestController.Api.Security;

namespace TestController.Api.Controllers;

/// <summary>
/// Exposes security status, current user identity, and effective capabilities.
/// </summary>
[ApiController]
[Route("api/security")]
public class SecurityController : ControllerBase
{
    private readonly IAuthenticationModeProvider _modeProvider;
    private readonly ISecurityAuditLogger _auditLogger;
    private readonly SecurityOptions _securityOptions;

    public SecurityController(
        IAuthenticationModeProvider modeProvider,
        ISecurityAuditLogger auditLogger,
        IOptions<SecurityOptions> securityOptions)
    {
        _modeProvider = modeProvider;
        _auditLogger = auditLogger;
        _securityOptions = securityOptions.Value;
    }

    /// <summary>GET /api/security/status — active mode, health, diagnostics.</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult GetSecurityStatus()
    {
        return Ok(new
        {
            mode = _modeProvider.Mode.ToString(),
            isHealthy = _modeProvider.IsHealthy,
            diagnostic = _modeProvider.GetDiagnosticStatus(),
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }

    /// <summary>GET /api/security/capabilities — effective permissions for the current user.</summary>
    [HttpGet("capabilities")]
    [Authorize(Policy = SecurityPolicies.User)]
    public IActionResult GetCapabilities()
    {
        var role = _modeProvider.ResolveRole(HttpContext.User);
        var identity = HttpContext.User.Identity?.Name ?? "unknown";

        var capabilities = new UserCapabilities
        {
            Identity = identity,
            Role = role.ToString(),
            CanReadDashboards = true,
            CanReadResults = true,
            CanTriggerPipeline = true,
            CanCancelOwnSession = true,
            CanCancelAnySession = role == UserRole.Admin,
            CanForceReleaseLock = role == UserRole.Admin,
            CanUnregisterAgent = role == UserRole.Admin,
            CanModifyWatchList = role == UserRole.Admin,
            CanViewAuditLog = role == UserRole.Admin,
        };

        return Ok(capabilities);
    }

    /// <summary>GET /api/security/whoami — current authenticated user info.</summary>
    [HttpGet("whoami")]
    [Authorize(Policy = SecurityPolicies.User)]
    public IActionResult WhoAmI()
    {
        var user = HttpContext.User;
        var role = _modeProvider.ResolveRole(user);

        return Ok(new
        {
            identity = user.Identity?.Name ?? "anonymous",
            isAuthenticated = user.Identity?.IsAuthenticated ?? false,
            authenticationType = user.Identity?.AuthenticationType ?? "none",
            role = role.ToString(),
            mode = _modeProvider.Mode.ToString(),
        });
    }

    /// <summary>GET /api/security/readiness — comprehensive diagnostic for deployment verification.</summary>
    [HttpGet("readiness")]
    [Authorize(Policy = SecurityPolicies.Admin)]
    public IActionResult GetReadiness()
    {
        var issues = new List<string>();

        // Auth provider health
        var authHealthy = _modeProvider.IsHealthy;
        if (!authHealthy)
            issues.Add($"Auth provider ({_modeProvider.Mode}) reports unhealthy: {_modeProvider.GetDiagnosticStatus()}");

        // Transport security
        var transport = _securityOptions.Transport;
        var certValid = true;
        string? certExpiry = null;

        if (transport.GrpcMode != GrpcTransportMode.Plaintext)
        {
            if (string.IsNullOrEmpty(transport.CertThumbprint) && string.IsNullOrEmpty(transport.CertFilePath))
            {
                certValid = false;
                issues.Add("TLS mode requires a certificate but none is configured.");
            }
            // If cert is configured, attempt to verify expiry
            if (!string.IsNullOrEmpty(transport.CertThumbprint))
            {
                try
                {
                    using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                        System.Security.Cryptography.X509Certificates.StoreName.My,
                        System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(
                        System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                        transport.CertThumbprint, false);
                    if (certs.Count == 0)
                    {
                        certValid = false;
                        issues.Add($"Certificate with thumbprint {transport.CertThumbprint} not found in LocalMachine/My store.");
                    }
                    else
                    {
                        var cert = certs[0];
                        certExpiry = cert.NotAfter.ToString("o");
                        if (cert.NotAfter < DateTime.UtcNow.AddDays(transport.CertExpiryWarningDays))
                            issues.Add($"Certificate expires {cert.NotAfter:yyyy-MM-dd} (within warning threshold of {transport.CertExpiryWarningDays} days).");
                    }
                }
                catch (Exception ex)
                {
                    certValid = false;
                    issues.Add($"Certificate store access failed: {ex.Message}");
                }
            }
        }

        // Rate limiting
        var rateLimit = _securityOptions.RateLimit;

        // Audit
        var audit = _securityOptions.Audit;
        if (audit.Enabled && !string.IsNullOrEmpty(audit.LogPath) && !System.IO.Directory.Exists(audit.LogPath))
            issues.Add($"Audit log path '{audit.LogPath}' does not exist.");

        var ready = authHealthy && certValid && issues.Count == 0;

        return Ok(new
        {
            ready,
            authMode = _modeProvider.Mode.ToString(),
            authProviderHealthy = authHealthy,
            authDiagnostic = _modeProvider.GetDiagnosticStatus(),
            rateLimitEnabled = rateLimit.Enabled,
            rateLimitPerMinute = rateLimit.RequestsPerMinute,
            adminRateLimitPerMinute = rateLimit.AdminRequestsPerMinute,
            transportMode = transport.GrpcMode.ToString(),
            certificateValid = certValid,
            certificateExpiry = certExpiry,
            auditEnabled = audit.Enabled,
            issues,
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }
}

public sealed class UserCapabilities
{
    public string Identity { get; set; } = "";
    public string Role { get; set; } = "";
    public bool CanReadDashboards { get; set; }
    public bool CanReadResults { get; set; }
    public bool CanTriggerPipeline { get; set; }
    public bool CanCancelOwnSession { get; set; }
    public bool CanCancelAnySession { get; set; }
    public bool CanForceReleaseLock { get; set; }
    public bool CanUnregisterAgent { get; set; }
    public bool CanModifyWatchList { get; set; }
    public bool CanViewAuditLog { get; set; }
}
