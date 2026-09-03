namespace TestControllerGrpc.Ado;

/// <summary>
/// Azure DevOps connection settings — bound from configuration section "Ado".
/// Never put secrets here (see <see cref="AuthMode"/> and Azure-DevOps-Integration-Guide.md Stage 3
/// for where each credential actually lives: Windows Credential Manager / env var / user-secrets).
/// </summary>
public sealed class AdoOptions
{
    /// <summary>Whether real ADO ingest is enabled. False (default) keeps the mock provider active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Organization name, e.g. "AVEVA-VSTS" (from https://dev.azure.com/AVEVA-VSTS).</summary>
    public string Organization { get; set; } = "";

    /// <summary>SP product project name, e.g. "System Platform".</summary>
    public string Project { get; set; } = "";

    /// <summary>Consumed-component (OMI) project name, e.g. "AppServer OMI". Empty falls back to <see cref="Project"/>.</summary>
    public string OmiProject { get; set; } = "";

    /// <summary>SP product repository to resolve changes against, e.g. "AASystemPlatformProduct".</summary>
    public string Repository { get; set; } = "";

    /// <summary>
    /// SP build definition id (legacy <c>spdefid</c>). Single-definition fallback used when <see cref="SpBuilds"/>
    /// is empty. When the ingest is SP-anchored it uses the latest builds of this definition (legacy "latest two
    /// SP builds" model) instead of a date-range scan. See Azure-DevOps-Integration-Guide.md Stage 2, first row.
    /// </summary>
    public int SpBuildDefinitionId { get; set; }

    /// <summary>How many recent SP builds to pull when definition-anchored (legacy compares the latest two).</summary>
    public int SpBuildHistoryCount { get; set; } = 2;

    /// <summary>
    /// Build-timeline log id whose text carries the consumed-component "Name : Version" manifest
    /// (legacy <c>spBuildLog</c>). Single-definition fallback used when <see cref="SpBuilds"/> is empty.
    /// </summary>
    public int SpBuildLogId { get; set; } = 24;

    /// <summary>
    /// The SP build definitions (legacy <c>SP.csv</c>: name → definition id → log id). When non-empty this
    /// replaces the single <see cref="SpBuildDefinitionId"/>/<see cref="SpBuildLogId"/> fields; the active line
    /// is chosen by <see cref="ActiveSpBuild"/> (or the first entry).
    /// </summary>
    public List<SpBuildDefinition> SpBuilds { get; set; } = [];

    /// <summary>Name of the active SP line in <see cref="SpBuilds"/> (e.g. "SP-2025"). Null selects the first entry.</summary>
    public string? ActiveSpBuild { get; set; }

    /// <summary>True when the ingest should anchor on an SP build definition rather than a date-range scan.</summary>
    public bool IsSpAnchored => SpBuilds.Count > 0 || SpBuildDefinitionId > 0;

    /// <summary>Resolves the effective SP definition id + log id from <see cref="SpBuilds"/> or the single fields.</summary>
    public (int DefinitionId, int LogId) ResolveSpBuild()
    {
        if (SpBuilds.Count > 0)
        {
            var selected = ActiveSpBuild is not null
                ? SpBuilds.FirstOrDefault(b => string.Equals(b.Name, ActiveSpBuild, StringComparison.OrdinalIgnoreCase))
                : SpBuilds[0];
            if (selected is not null)
                return (selected.DefinitionId, selected.LogId);
        }
        return (SpBuildDefinitionId, SpBuildLogId);
    }

    /// <summary>Which <see cref="IAdoTokenProvider"/> to register. See ADR-03.</summary>
    public AdoAuthMode AuthMode { get; set; } = AdoAuthMode.Pat;

    /// <summary>Impact model: component-centric change scan (default) or the legacy SP-manifest diff.</summary>
    public AdoCollectionMode CollectionMode { get; set; } = AdoCollectionMode.Components;

    /// <summary>Release branches the user can switch between (e.g. SP2026, SP2023R2SP2). Empty = no branch filter.</summary>
    public List<string> Branches { get; set; } = [];

    /// <summary>Globs limiting which LIVE source branches appear in the branch switcher. Default: Release(s)/*, prod/* only. Empty = show all.</summary>
    public List<string> BranchIncludePatterns { get; set; } = ["releases/*", "release/*", "prod/*"];

    /// <summary>Branch to preselect in the switcher on load (must match a live branch name). Empty/not-found falls back to "(all branches)".</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>Globs for files to hide from the modified-files list (pipeline yaml, package manifests, shared configs). The repo's own &lt;RepoName&gt;.yaml is always ignored.</summary>
    public List<string> IgnoredFilePatterns { get; set; } = ["*.yml", "*.yaml", "*Universal-Package*.json"];

    /// <summary>Entra tenant id — required for ServicePrincipal and Interactive modes.</summary>
    public string? TenantId { get; set; }

    /// <summary>Entra app (client) id — required for ServicePrincipal and Interactive modes.</summary>
    public string? ClientId { get; set; }

    /// <summary>Certificate thumbprint (CurrentUser\My) for ServicePrincipal mode — preferred over a secret.</summary>
    public string? ClientCertificateThumbprint { get; set; }

    /// <summary>
    /// Name of the environment variable holding the client secret (ServicePrincipal fallback) or the PAT
    /// (Pat mode). Never the secret value itself. Default "ADO_PAT" / "ADO_CLIENT_SECRET".
    /// </summary>
    public string? SecretEnvVarName { get; set; }

    /// <summary>Entra resource id for Azure DevOps (public constant, ADR-04).</summary>
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    public string BaseUrl => $"https://dev.azure.com/{Organization}/";
}

/// <summary>One row of the legacy <c>SP.csv</c>: an SP product line's build definition and manifest log id.</summary>
public sealed class SpBuildDefinition
{
    public string Name { get; set; } = "";
    public int DefinitionId { get; set; }
    public int LogId { get; set; }
}

public enum AdoAuthMode
{
    /// <summary>Personal Access Token — dev/fallback only, never appsettings.json.</summary>
    Pat,
    /// <summary>Entra service principal via certificate or client secret — for the Web/scheduler host.</summary>
    ServicePrincipal,
    /// <summary>Delegated interactive browser sign-in — for the WPF host only (ADR-03).</summary>
    Interactive,
}

public enum AdoCollectionMode
{
    /// <summary>Scan each component pipeline's recent builds for changes (works with the current ADO pipelines).</summary>
    Components,
    /// <summary>Legacy SP consumed-manifest diff between two SP builds (not used by the Universal-Packages SP pipeline).</summary>
    SpManifest,
}
