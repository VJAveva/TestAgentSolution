namespace TestControllerGrpc.Core.Maintenance;

/// <summary>Result of an online update search on a node.</summary>
public sealed record UpdateSearchResult(bool Ok, int AvailableCount, IReadOnlyList<string> Titles, string? Error = null)
{
    public static UpdateSearchResult Failed(string error) => new(false, 0, [], error);
    public static UpdateSearchResult None { get; } = new(true, 0, []);
}

/// <summary>Result of installing updates on a node.</summary>
public sealed record UpdateInstallResult(
    bool Ok, int InstalledCount, int FailedCount, bool RebootRequired, string? Error = null)
{
    public static UpdateInstallResult Failed(string error) => new(false, 0, 0, false, error);
}

/// <summary>
/// Installs Windows updates on an agent node. Separate from <see cref="INodeUpdateStatusStore"/>, which only
/// observes posture: this is the first capability that <em>changes</em> a node's patch state.
/// </summary>
public interface INodeUpdateInstaller
{
    /// <summary>
    /// Whether this node can install updates at all. Agents provisioned by Setup-InteractiveAgent.ps1 run under
    /// an auto-logon user rather than LocalSystem, so the capability is per-node and must never be assumed.
    /// </summary>
    Task<bool> IsInstallSupportedAsync(string nodeId, CancellationToken ct);

    /// <summary>Online search. The detector's cached (<c>Online = false</c>) search is not sufficient here.</summary>
    Task<UpdateSearchResult> SearchAsync(string nodeId, CancellationToken ct);

    Task<UpdateInstallResult> InstallAsync(string nodeId, CancellationToken ct);
}

/// <summary>Everything needed to refresh one node's golden image.</summary>
public sealed record GoldenImageRefreshRequest
{
    public required string NodeId { get; init; }

    /// <summary>Name for the new baseline. Ignored on platforms without named snapshots.</summary>
    public string? NewSnapshotName { get; init; }

    public string? BaselineSnapshotName { get; init; }

    /// <summary>Abort an in-flight run on the node rather than refusing at precheck.</summary>
    public bool ForceIfBusy { get; init; }

    /// <summary>
    /// Proceed even when the search finds nothing. Off by default: refreshing a baseline that gained no
    /// updates burns the rollback point for no benefit.
    /// </summary>
    public bool RefreshWhenNoUpdates { get; init; }

    public MaintenanceTriggerSource TriggerSource { get; init; } = MaintenanceTriggerSource.FleetPanel;
    public string TriggeredBy { get; init; } = "";
    public string? Reason { get; init; }
    public TimeSpan AgentWaitTimeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Runs the golden-image refresh state machine for one node.</summary>
public interface IGoldenImageRefreshOperation
{
    Task<MaintenanceOperation> ExecuteAsync(
        MaintenanceOperation operation,
        GoldenImageRefreshRequest request,
        IProgress<MaintenanceProgress> progress,
        CancellationToken cancellationToken);
}
