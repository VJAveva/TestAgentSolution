using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

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

/// <summary>Deploy/health snapshot for the Regression tab: config state, loaded component count, and a live ADO probe.</summary>
public sealed record RegressionHealth(
    bool Enabled,
    bool CredentialConfigured,
    string CredentialSource,
    int ComponentCount,
    bool AdoReachable,
    string? ProbeError,
    DateTimeOffset CheckedUtc);

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

    /// <summary>Deploy sanity probe: loaded-component count + a real lightweight authenticated ADO call.</summary>
    Task<RegressionHealth> CheckHealthAsync(CancellationToken ct);
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
        var branches = new List<string>(_options.Branches); // curated release branches are always shown
        if (_options.Enabled && _services.GetService(typeof(IBuildQueries)) is IBuildQueries builds)
        {
            try
            {
                var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
                var live = await builds.GetRecentSourceBranchesAsync(omiProject, 200, ct);

                // Blank entries are dropped first: a config of [""] is not the same as [], and an empty
                // glob matches nothing (GlobUtil.IsMatch), which would silently reject every live branch.
                var includePatterns = _options.BranchIncludePatterns
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                // Only surface Release/*, prod/* live branches (Ado:BranchIncludePatterns) — hide feature/user branches.
                branches.AddRange(includePatterns.Count == 0
                    ? live
                    : live.Where(b => MatchesBranchInclude(b, includePatterns)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: fall back to configured branches. Logged because an empty fallback
                // (the default) makes an ADO credential failure look like "no branches exist".
                (_services.GetService(typeof(IAppLogger)) as IAppLogger)?.Warn(
                    "Ado",
                    $"Branch list fell back to Ado:Branches ({_options.Branches.Count} configured) — " +
                    $"live lookup failed: {ex.Message}");
            }
        }
        return branches
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // A live branch qualifies when it matches any Ado:BranchIncludePatterns glob (refs/heads/ prefix tolerated).
    private static bool MatchesBranchInclude(string branch, IReadOnlyList<string> patterns)
    {
        var normalized = branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;
        return patterns.Any(p => GlobUtil.IsMatch(normalized, p) || GlobUtil.IsMatch(branch, p));
    }

    public async Task<RegressionHealth> CheckHealthAsync(CancellationToken ct)
    {
        var info = GetConnectionInfo();
        var componentCount = GetComponents().Count;
        var reachable = false;
        string? probeError = null;

        if (!_options.Enabled)
        {
            probeError = "ADO ingest disabled (Ado:Enabled=false) — serving mock/empty data.";
        }
        else if (_services.GetService(typeof(IBuildQueries)) is IBuildQueries builds)
        {
            try
            {
                // Lightweight authenticated probe: a 1-row recent-builds query proves connectivity + credential.
                var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
                _ = await builds.GetRecentSourceBranchesAsync(omiProject, 1, ct);
                reachable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                probeError = ex.Message;
            }
        }
        else
        {
            probeError = "ADO enabled but IBuildQueries is not registered — check credential resolution at startup.";
        }

        return new RegressionHealth(
            Enabled: info.Enabled,
            CredentialConfigured: info.CredentialConfigured,
            CredentialSource: info.CredentialSource,
            ComponentCount: componentCount,
            AdoReachable: reachable,
            ProbeError: probeError,
            CheckedUtc: DateTimeOffset.UtcNow);
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
