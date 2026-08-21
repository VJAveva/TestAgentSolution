using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado;

/// <summary>Connection/source state for the Regression tab status bar — what ADO we're talking to and how.</summary>
public sealed record RegressionConnectionInfo(
    bool Enabled,
    string Mode,
    string Organization,
    string Project,
    string OmiProject,
    string AuthMode,
    string CredentialSource,
    bool CredentialConfigured);

/// <summary>A selectable component/definition for the tab's definition dropdown.</summary>
public sealed record RegressionComponentRef(string Name, int DefinitionId);

/// <summary>A selectable build for the tab's build picker.</summary>
public sealed record RegressionBuildRef(int BuildId, string BuildNumber, string Result, DateTimeOffset? FinishedUtc)
{
    public string Display => $"{BuildNumber}" + (FinishedUtc is { } f ? $"  ·  {f.LocalDateTime:g}" : "") +
        (string.IsNullOrEmpty(Result) ? "" : $"  ·  {Result}");
}

/// <summary>Exposes ADO connection state and the tracked component list to the UI (status bar + definition picker).</summary>
public interface IRegressionSourceCatalog
{
    RegressionConnectionInfo GetConnectionInfo();
    IReadOnlyList<RegressionComponentRef> GetComponents();

    /// <summary>Recent builds for a component definition (build picker). Empty when ADO ingest is disabled.</summary>
    Task<IReadOnlyList<RegressionBuildRef>> GetComponentBuildsAsync(int definitionId, CancellationToken ct);

    /// <summary>Impact for a specifically-picked component build. Null when unavailable/disabled.</summary>
    Task<SubsystemRow?> GetComponentBuildImpactAsync(int definitionId, int buildId, CancellationToken ct);

    /// <summary>Release branches for the parallel-dev branch switcher (queried from recent ADO builds + config).</summary>
    Task<IReadOnlyList<string>> GetBranchesAsync(CancellationToken ct);
}

public sealed class RegressionSourceCatalog : IRegressionSourceCatalog
{
    private const int BuildPickerCount = 15;

    private readonly AdoOptions _options;
    private readonly IComponentBuildMap _map;
    private readonly IServiceProvider _services;

    public RegressionSourceCatalog(IOptions<AdoOptions> options, IComponentBuildMap map, IServiceProvider services)
    {
        _options = options.Value;
        _map = map;
        _services = services;
    }

    public RegressionConnectionInfo GetConnectionInfo()
    {
        var credentialSource = "none";
        var configured = false;

        if (_options.Enabled)
        {
            // Describe the resolved credential without triggering a token request; tolerate a disabled pipeline.
            var provider = _services.GetService(typeof(IAdoTokenProvider)) as IAdoTokenProvider;
            credentialSource = provider?.Describe() ?? _options.AuthMode.ToString();
            configured = _options.AuthMode switch
            {
                AdoAuthMode.Pat => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(_options.SecretEnvVarName ?? "ADO_PAT")),
                AdoAuthMode.Interactive => (_services.GetService(typeof(IInteractiveAdoAuthenticator)) as IInteractiveAdoAuthenticator)?.IsSignedIn ?? false,
                _ => !string.IsNullOrWhiteSpace(_options.TenantId) && !string.IsNullOrWhiteSpace(_options.ClientId),
            };
        }

        return new RegressionConnectionInfo(
            Enabled: _options.Enabled,
            Mode: _options.CollectionMode.ToString(),
            Organization: _options.Organization,
            Project: _options.Project,
            OmiProject: string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject,
            AuthMode: _options.AuthMode.ToString(),
            CredentialSource: credentialSource,
            CredentialConfigured: configured);
    }

    public IReadOnlyList<RegressionComponentRef> GetComponents() =>
        _map.All()
            .Where(c => c.BuildDefinitionId > 0)
            .OrderBy(c => c.ComponentId, StringComparer.OrdinalIgnoreCase)
            .Select(c => new RegressionComponentRef(c.ComponentId, c.BuildDefinitionId))
            .ToList();

    public async Task<IReadOnlyList<string>> GetBranchesAsync(CancellationToken ct)
    {
        var branches = new List<string>(_options.Branches);
        if (_options.Enabled && _services.GetService(typeof(IBuildQueries)) is IBuildQueries builds)
        {
            try
            {
                var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
                branches.AddRange(await builds.GetRecentSourceBranchesAsync(omiProject, 200, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best-effort: fall back to configured branches
            }
        }
        return branches
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<RegressionBuildRef>> GetComponentBuildsAsync(int definitionId, CancellationToken ct)
    {
        if (_services.GetService(typeof(ComponentChangeCollector)) is not ComponentChangeCollector collector)
            return [];
        return await collector.GetComponentBuildsAsync(definitionId, BuildPickerCount, ct);
    }

    public async Task<SubsystemRow?> GetComponentBuildImpactAsync(int definitionId, int buildId, CancellationToken ct)
    {
        if (_services.GetService(typeof(ComponentChangeCollector)) is not ComponentChangeCollector collector)
            return null;
        return await collector.CollectComponentBuildAsync(definitionId, buildId, ct);
    }
}
