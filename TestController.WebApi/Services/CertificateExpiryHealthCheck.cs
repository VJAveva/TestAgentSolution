using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestController.Api.Security;

namespace TestController.WebApi.Services;

/// <summary>
/// Health check that monitors TLS certificate expiry.
/// Reports degraded when cert is within warning threshold, unhealthy when expired.
/// </summary>
public sealed class CertificateExpiryHealthCheck : IHealthCheck
{
    private readonly TransportSecurityOptions _transport;
    private readonly ILogger<CertificateExpiryHealthCheck> _logger;

    public CertificateExpiryHealthCheck(
        IOptions<SecurityOptions> securityOptions,
        ILogger<CertificateExpiryHealthCheck> logger)
    {
        _transport = securityOptions.Value.Transport;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_transport.GrpcMode == GrpcTransportMode.Plaintext)
        {
            return Task.FromResult(HealthCheckResult.Healthy("TLS not configured (plaintext mode)."));
        }

        try
        {
            var cert = LoadCertificate();
            if (cert == null)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("TLS certificate not found."));
            }

            var daysUntilExpiry = (cert.NotAfter - DateTime.UtcNow).TotalDays;

            if (daysUntilExpiry <= 0)
            {
                _logger.LogError("TLS certificate EXPIRED on {ExpiryDate}. Subject: {Subject}",
                    cert.NotAfter, cert.Subject);
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Certificate expired on {cert.NotAfter:yyyy-MM-dd}. Subject: {cert.Subject}"));
            }

            if (daysUntilExpiry <= _transport.CertExpiryWarningDays)
            {
                _logger.LogWarning("TLS certificate expires in {Days:F0} days on {ExpiryDate}. Subject: {Subject}",
                    daysUntilExpiry, cert.NotAfter, cert.Subject);
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Certificate expires in {daysUntilExpiry:F0} days on {cert.NotAfter:yyyy-MM-dd}. Subject: {cert.Subject}"));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Certificate valid until {cert.NotAfter:yyyy-MM-dd} ({daysUntilExpiry:F0} days remaining)."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check TLS certificate expiry");
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to check certificate.", ex));
        }
    }

    private X509Certificate2? LoadCertificate()
    {
        if (!string.IsNullOrWhiteSpace(_transport.CertFilePath))
        {
            if (!File.Exists(_transport.CertFilePath))
                return null;

            return string.IsNullOrEmpty(_transport.CertPassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(_transport.CertFilePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(_transport.CertFilePath, _transport.CertPassword);
        }

        if (!string.IsNullOrWhiteSpace(_transport.CertThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(X509FindType.FindByThumbprint, _transport.CertThumbprint, validOnly: false);
            return certs.Count > 0 ? certs[0] : null;
        }

        return null;
    }
}
