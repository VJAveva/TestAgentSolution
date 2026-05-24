namespace TestController.Api.Security;

/// <summary>
/// Root configuration for the multi-identity security framework.
/// Bound to the "Security" section in appsettings.json.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Active authentication mode: None (no auth), Domain, Local, or Token.</summary>
    public AuthMode AuthMode { get; set; } = AuthMode.None;

    public DomainAuthOptions Domain { get; set; } = new();
    public LocalAuthOptions Local { get; set; } = new();
    public TokenAuthOptions Token { get; set; } = new();
    public SecurityAuditOptions Audit { get; set; } = new();
    public TransportSecurityOptions Transport { get; set; } = new();
    public RateLimitSecurityOptions RateLimit { get; set; } = new();
}

public enum AuthMode
{
    /// <summary>No authentication — all requests treated as Admin. Use for development/trusted networks.</summary>
    None,
    Domain,
    Local,
    Token
}

/// <summary>Domain (Active Directory) authentication settings.</summary>
public sealed class DomainAuthOptions
{
    /// <summary>Required AD domain name (e.g., "XYZ").</summary>
    public string RequireDomain { get; set; } = "";

    /// <summary>AD group whose members get Admin policy (e.g., "XYZ\\TestAdmins").</summary>
    public string AdminGroup { get; set; } = "";

    /// <summary>AD group whose members get User policy (e.g., "XYZ\\TestUsers").</summary>
    public string UserGroup { get; set; } = "";

    /// <summary>Fall back to Local mode if AD is unreachable.</summary>
    public bool FallbackToLocal { get; set; } = true;

    /// <summary>Minutes to cache group membership lookups.</summary>
    public int GroupCacheMinutes { get; set; } = 10;
}

/// <summary>Local (SAM database) authentication settings.</summary>
public sealed class LocalAuthOptions
{
    /// <summary>Local group whose members get Admin policy (e.g., ".\\Administrators").</summary>
    public string AdminGroup { get; set; } = ".\\Administrators";

    /// <summary>Require HTTPS when using Basic Auth.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Minimum password length for local accounts.</summary>
    public int MinPasswordLength { get; set; } = 12;
}

/// <summary>Token-based (bearer) authentication settings.</summary>
public sealed class TokenAuthOptions
{
    /// <summary>Path to the DPAPI-encrypted token store file.</summary>
    public string TokenStore { get; set; } = "secrets.json";

    /// <summary>Token rotation interval in days.</summary>
    public int TokenRotationDays { get; set; } = 30;

    /// <summary>Enforce HTTPS for bearer token transport.</summary>
    public bool EnforceHttps { get; set; } = true;
}

/// <summary>Security audit logging configuration.</summary>
public sealed class SecurityAuditOptions
{
    /// <summary>Enable security audit logging.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Audit log retention in days.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Audit log directory path.</summary>
    public string LogPath { get; set; } = @"C:\TestControllerService\Logs";
}

/// <summary>Transport encryption settings for gRPC communication.</summary>
public sealed class TransportSecurityOptions
{
    /// <summary>gRPC transport mode: Plaintext, PlaintextAndTls, TlsOnly.</summary>
    public GrpcTransportMode GrpcMode { get; set; } = GrpcTransportMode.Plaintext;

    /// <summary>Server certificate thumbprint for TLS (from LocalMachine/My store).</summary>
    public string CertThumbprint { get; set; } = "";

    /// <summary>PFX file path for the server certificate (alternative to thumbprint).</summary>
    public string CertFilePath { get; set; } = "";

    /// <summary>Password for the PFX file (if applicable).</summary>
    public string CertPassword { get; set; } = "";

    /// <summary>Whether mutual TLS (client cert) is required for agent connections.</summary>
    public bool RequireMutualTls { get; set; }

    /// <summary>Trusted CA certificate thumbprint for validating client certs in mTLS.</summary>
    public string TrustedCaThumbprint { get; set; } = "";

    /// <summary>TLS port for agents (used alongside plaintext during migration).</summary>
    public int TlsPort { get; set; } = 5443;

    /// <summary>Days before cert expiry to emit a warning.</summary>
    public int CertExpiryWarningDays { get; set; } = 30;
}

/// <summary>gRPC transport encryption mode.</summary>
public enum GrpcTransportMode
{
    /// <summary>No encryption — plaintext HTTP/2 only.</summary>
    Plaintext,
    /// <summary>Both plaintext and TLS listeners active (migration period).</summary>
    PlaintextAndTls,
    /// <summary>TLS only — plaintext listener disabled.</summary>
    TlsOnly
}

/// <summary>Rate limiting settings for security.</summary>
public sealed class RateLimitSecurityOptions
{
    /// <summary>Enable rate limiting.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum requests per minute for standard users.</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Maximum requests per minute for admin users.</summary>
    public int AdminRequestsPerMinute { get; set; } = 300;
}
