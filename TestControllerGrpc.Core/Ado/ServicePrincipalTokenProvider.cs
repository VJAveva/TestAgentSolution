using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Entra service-principal auth for the Web/scheduler host (ADR-03). Prefers a certificate
/// (<see cref="ClientCertificateCredential"/>) over a client secret; falls back to
/// <see cref="ClientSecretCredential"/> only if no certificate thumbprint is configured.
/// The service principal must already be added as a user in the ADO organization, or every
/// call fails with 401 even with a valid Entra token (see Azure-DevOps-Integration-Guide.md Stage 3).
/// </summary>
public sealed class ServicePrincipalTokenProvider : IAdoTokenProvider
{
    private static readonly string[] Scopes = [$"{AdoOptions.AdoResourceId}/.default"];

    private readonly AdoOptions _options;
    private TokenCredential? _credential;
    private string _describe = "Service principal";

    public ServicePrincipalTokenProvider(IOptions<AdoOptions> options) => _options = options.Value;

    // Lazy: never build the credential (or load the cert) in the constructor — a misconfig must not crash startup.
    private TokenCredential Credential()
    {
        if (_credential is not null)
            return _credential;

        var o = _options;
        if (string.IsNullOrWhiteSpace(o.TenantId) || string.IsNullOrWhiteSpace(o.ClientId))
            throw new AdoCredentialMissingException(
                "ServicePrincipal auth requires Ado:TenantId and Ado:ClientId to be configured.");

        if (!string.IsNullOrWhiteSpace(o.ClientCertificateThumbprint))
        {
            var cert = CertificateLoader.FindByThumbprint(o.ClientCertificateThumbprint);
            _describe = $"Service principal (cert {o.ClientCertificateThumbprint[..8]}…)";
            return _credential = new ClientCertificateCredential(o.TenantId, o.ClientId, cert);
        }

        var envVar = o.SecretEnvVarName ?? "ADO_CLIENT_SECRET";
        var secret = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(secret))
            throw new AdoCredentialMissingException(
                $"ServicePrincipal auth needs either Ado:ClientCertificateThumbprint (preferred) or the " +
                $"'{envVar}' environment variable set to the client secret.");

        _describe = "Service principal (client secret)";
        return _credential = new ClientSecretCredential(o.TenantId, o.ClientId, secret);
    }

    public async Task<string> GetAuthHeaderAsync(CancellationToken ct)
    {
        var token = await Credential().GetTokenAsync(new TokenRequestContext(Scopes), ct);
        return $"Bearer {token.Token}";
    }

    public string Describe() => _describe;
}

internal static class CertificateLoader
{
    public static System.Security.Cryptography.X509Certificates.X509Certificate2 FindByThumbprint(string thumbprint)
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(
            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count == 0)
            throw new AdoCredentialMissingException($"No certificate with thumbprint '{thumbprint}' found in CurrentUser\\My.");
        return matches[0];
    }
}
