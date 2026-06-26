namespace TestController.WebApi.Services;

/// <summary>
/// The authoritative deployment topology for the standalone WebApi host. Makes
/// the co-located vs. standalone choice an explicit, validated decision rather
/// than something silently inferred from whether <c>ControllerProxyUrl</c> is set.
/// </summary>
public enum DeploymentTopology
{
    /// <summary>
    /// Resolve from configuration: <see cref="DeploymentOptions.ControllerProxyUrl"/>
    /// present ⇒ <see cref="CoLocated"/>, absent ⇒ <see cref="Standalone"/>.
    /// Preserves historical behaviour while still logging the resolved mode.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// This WebApi runs alongside the WPF Controller and proxies execution,
    /// dashboard, and fleet calls to it (<c>ControllerProxyUrl</c> required).
    /// The WPF Controller is the single authority and DB owner.
    /// </summary>
    CoLocated = 1,

    /// <summary>
    /// This WebApi is the sole host: it executes runs locally via
    /// <c>StandalonePipelineExecutor</c>, owns its own lock authority, and runs
    /// with NO WPF Controller anywhere. <c>ControllerProxyUrl</c> must be empty.
    /// </summary>
    Standalone = 2,
}

/// <summary>
/// Strongly-typed deployment settings bound from the "Deployment" configuration
/// section. Surfaces the topology decision and the co-located controller URL in
/// one place so startup can validate and log the effective mode.
/// </summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>Declared topology. Defaults to <see cref="DeploymentTopology.Auto"/>.</summary>
    public DeploymentTopology Topology { get; set; } = DeploymentTopology.Auto;

    /// <summary>
    /// Base URL of the co-located WPF Controller. Bound from the top-level
    /// <c>ControllerProxyUrl</c> key (legacy location) so existing deployments
    /// keep working; mirrored here for validation against <see cref="Topology"/>.
    /// </summary>
    public string? ControllerProxyUrl { get; set; }

    /// <summary>
    /// Resolves the effective topology, expanding <see cref="DeploymentTopology.Auto"/>
    /// from whether a controller proxy URL is configured.
    /// </summary>
    public DeploymentTopology ResolveEffective()
    {
        if (Topology != DeploymentTopology.Auto)
            return Topology;
        return string.IsNullOrWhiteSpace(ControllerProxyUrl)
            ? DeploymentTopology.Standalone
            : DeploymentTopology.CoLocated;
    }
}
