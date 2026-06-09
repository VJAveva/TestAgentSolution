namespace TestController.WebApi.Services;

/// <summary>
/// Validates production configuration on startup. Logs warnings for
/// missing/invalid settings and fails fast on critical misconfigurations.
/// </summary>
public sealed class ConfigValidator
{
    private readonly IConfiguration _config;
    private readonly ILogger<ConfigValidator> _logger;

    public ConfigValidator(IConfiguration config, ILogger<ConfigValidator> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Validates configuration and returns a list of issues found.
    /// Critical issues will cause the application to fail fast.
    /// </summary>
    public ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Critical: Agents section must exist and have at least one entry
        var agentsSection = _config.GetSection("Agents");
        var agentChildren = agentsSection.GetChildren().ToList();
        if (agentChildren.Count == 0)
        {
            errors.Add("No agents configured in 'Agents' section. At least one agent is required.");
        }
        else
        {
            foreach (var child in agentChildren)
            {
                var name = child["Name"];
                var address = child["Address"];
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add($"Agent entry missing 'Name' property.");
                if (string.IsNullOrWhiteSpace(address))
                    errors.Add($"Agent '{name ?? "(unnamed)"}' missing 'Address' property.");
                else if (!Uri.TryCreate(address, UriKind.Absolute, out _))
                    errors.Add($"Agent '{name}' has invalid 'Address': {address}");
            }
        }

        // Critical: VocabularyFile must be specified
        var vocabFile = _config["VocabularyFile"];
        if (string.IsNullOrWhiteSpace(vocabFile))
        {
            errors.Add("'VocabularyFile' path is not configured.");
        }
        else if (!File.Exists(vocabFile))
        {
            warnings.Add($"VocabularyFile '{vocabFile}' does not exist on disk. It will be created on first save.");
        }

        // Warning: LogDirectory should be writable
        var logDir = _config["LogDirectory"];
        if (string.IsNullOrWhiteSpace(logDir))
        {
            warnings.Add("'LogDirectory' not configured. Default path will be used.");
        }
        else if (!Directory.Exists(logDir))
        {
            try
            {
                Directory.CreateDirectory(logDir);
                warnings.Add($"Created LogDirectory: {logDir}");
            }
            catch (Exception ex)
            {
                errors.Add($"Cannot create LogDirectory '{logDir}': {ex.Message}");
            }
        }

        // Warning: CORS origins should be specified in production
        var allowedOrigins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins is null or { Length: 0 })
        {
            warnings.Add("Cors:AllowedOrigins not configured. All origins will be allowed.");
        }

        // Warning: BuildResults configuration
        var resultsRoot = _config["BuildResults:ResultsRootPath"];
        if (!string.IsNullOrWhiteSpace(resultsRoot) && !Directory.Exists(resultsRoot))
        {
            warnings.Add($"BuildResults:ResultsRootPath '{resultsRoot}' does not exist.");
        }

        // Warning: SignalR settings
        var signalRSection = _config.GetSection("SignalR");
        if (!signalRSection.Exists())
        {
            warnings.Add("SignalR section not configured. Defaults will be used.");
        }

        // Security configuration validation
        ValidateSecurityConfig(errors, warnings);

        // Log results
        foreach (var w in warnings)
            _logger.LogWarning("[ConfigValidator] {Warning}", w);

        foreach (var e in errors)
            _logger.LogError("[ConfigValidator] {Error}", e);

        return new ConfigValidationResult(errors, warnings);
    }

    private void ValidateSecurityConfig(List<string> errors, List<string> warnings)
    {
        var secSection = _config.GetSection("Security");
        if (!secSection.Exists())
        {
            warnings.Add("Security section not configured. AuthMode=None (no authentication) will be used.");
            return;
        }

        var authMode = secSection["AuthMode"];
        if (string.IsNullOrWhiteSpace(authMode))
        {
            warnings.Add("Security:AuthMode not set. Defaulting to None (no authentication).");
        }

        // Domain mode validation
        if (string.Equals(authMode, "Domain", StringComparison.OrdinalIgnoreCase))
        {
            var domain = secSection["Domain:RequireDomain"];
            if (string.IsNullOrWhiteSpace(domain))
                errors.Add("Security:Domain:RequireDomain must be specified when AuthMode=Domain.");

            var adminGroup = secSection["Domain:AdminGroup"];
            if (string.IsNullOrWhiteSpace(adminGroup))
                warnings.Add("Security:Domain:AdminGroup not set. No users will have Admin role in Domain mode.");
        }

        // Token mode validation
        if (string.Equals(authMode, "Token", StringComparison.OrdinalIgnoreCase))
        {
            var tokenStore = secSection["Token:TokenStore"];
            if (string.IsNullOrWhiteSpace(tokenStore))
                warnings.Add("Security:Token:TokenStore not set. Default 'secrets.json' will be used.");
        }

        // Transport security validation
        var grpcMode = secSection["Transport:GrpcMode"];
        if (string.Equals(grpcMode, "TlsOnly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(grpcMode, "PlaintextAndTls", StringComparison.OrdinalIgnoreCase))
        {
            var thumbprint = secSection["Transport:CertThumbprint"];
            var certFile = secSection["Transport:CertFilePath"];
            if (string.IsNullOrWhiteSpace(thumbprint) && string.IsNullOrWhiteSpace(certFile))
                errors.Add("Security:Transport requires CertThumbprint or CertFilePath when GrpcMode involves TLS.");
        }

        // Rate limit validation
        var rateEnabled = secSection["RateLimit:Enabled"];
        if (string.Equals(rateEnabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(secSection["RateLimit:RequestsPerMinute"], out var rpm) && rpm < 10)
                warnings.Add($"Security:RateLimit:RequestsPerMinute={rpm} is very low and may cause throttling in normal use.");
        }
    }
}

public sealed record ConfigValidationResult(List<string> Errors, List<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Detailed security readiness report returned by the /api/security/readiness endpoint.
/// </summary>
public sealed class SecurityReadinessReport
{
    public bool Ready { get; set; }
    public string AuthMode { get; set; } = "";
    public bool AuthProviderHealthy { get; set; }
    public string AuthDiagnostic { get; set; } = "";
    public bool RateLimitEnabled { get; set; }
    public int RateLimitPerMinute { get; set; }
    public int AdminRateLimitPerMinute { get; set; }
    public string TransportMode { get; set; } = "";
    public bool CertificateValid { get; set; }
    public string? CertificateExpiry { get; set; }
    public bool AuditEnabled { get; set; }
    public List<string> Issues { get; set; } = [];
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
}
