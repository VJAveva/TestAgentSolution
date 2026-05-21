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

        // Log results
        foreach (var w in warnings)
            _logger.LogWarning("[ConfigValidator] {Warning}", w);

        foreach (var e in errors)
            _logger.LogError("[ConfigValidator] {Error}", e);

        return new ConfigValidationResult(errors, warnings);
    }
}

public sealed record ConfigValidationResult(List<string> Errors, List<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}
