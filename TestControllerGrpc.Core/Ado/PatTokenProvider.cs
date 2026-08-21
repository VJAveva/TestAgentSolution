using System.Text;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Ado;

/// <summary>
/// PAT-based auth (dev/fallback only — ADR per Azure-DevOps-Integration-Guide.md Stage 3).
/// Reads the token via an injected <see cref="IAdoCredentialStore"/>, never IConfiguration directly,
/// so callers can swap in Windows Credential Manager / env var / user-secrets without touching this class.
/// </summary>
public sealed class PatTokenProvider : IAdoTokenProvider
{
    private readonly IAdoCredentialStore _store;
    private readonly AdoOptions _options;

    public PatTokenProvider(IAdoCredentialStore store, IOptions<AdoOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public Task<string> GetAuthHeaderAsync(CancellationToken ct)
    {
        var pat = _store.GetPat();
        if (string.IsNullOrWhiteSpace(pat))
        {
            var envVar = _options.SecretEnvVarName ?? "ADO_PAT";
            throw new AdoCredentialMissingException(
                $"No PAT found. Set the '{envVar}' environment variable to a PAT with Build(Read)/Code(Read)/" +
                "WorkItems(Read)/TestManagement(Read) scopes, or configure Windows Credential Manager. " +
                "Never put the PAT in appsettings.json.");
        }

        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        return Task.FromResult($"Basic {basic}");
    }

    public string Describe() => "PAT (Basic auth)";
}

/// <summary>
/// Abstraction over where the PAT is actually stored, per Stage 3: Windows Credential Manager (WPF),
/// environment variable / protected config (service), or user-secrets (dev only) — never appsettings.json.
/// </summary>
public interface IAdoCredentialStore
{
    string? GetPat();
}

/// <summary>Default store: reads from an environment variable only. Sufficient for the Web host and CI.</summary>
public sealed class EnvironmentAdoCredentialStore : IAdoCredentialStore
{
    private readonly AdoOptions _options;

    public EnvironmentAdoCredentialStore(IOptions<AdoOptions> options) => _options = options.Value;

    // Trim stray whitespace/newlines that setx or copy-paste can leave on the value.
    public string? GetPat() => Environment.GetEnvironmentVariable(_options.SecretEnvVarName ?? "ADO_PAT")?.Trim();
}
