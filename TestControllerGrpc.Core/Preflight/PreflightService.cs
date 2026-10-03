using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Preflight;

/// <summary>
/// Composes live controller state into the facts <see cref="PreflightRunner"/> needs, so both hosts
/// ask the same question of the same sources.
/// </summary>
/// <remarks>
/// Every source here is already in memory: the agent roster, the heartbeat telemetry cache, the two
/// lock layers and the maintenance store. Pre-flight therefore opens no connection and touches no
/// agent, which is what keeps a 9-node fleet - or a 90-node one - inside the 60s budget.
/// </remarks>
public sealed class PreflightService
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IAgentTelemetryCache? _telemetry;
    private readonly AgentLockManager _agentLocks;
    private readonly IMaintenanceStateStore? _maintenance;
    private readonly INodeUpdateStatusStore? _updates;
    private readonly ILockRegistry _lockRegistry;
    private readonly IPreflightFileSystem _fs;

    public PreflightService(
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager agentLocks,
        ILockRegistry lockRegistry,
        IAgentTelemetryCache? telemetry = null,
        IMaintenanceStateStore? maintenance = null,
        INodeUpdateStatusStore? updates = null,
        IPreflightFileSystem? fileSystem = null)
    {
        _dispatcher = dispatcher;
        _agentLocks = agentLocks;
        _lockRegistry = lockRegistry;
        _telemetry = telemetry;
        _maintenance = maintenance;
        _updates = updates;
        _fs = fileSystem ?? new PreflightFileSystem();
    }

    /// <summary>Minimum free space before a run is warned about. Configurable per deployment.</summary>
    public double MinimumDiskGb { get; set; } = 15;

    public PreflightReport Check(
        WatchListConfig config,
        WatchItemConfig? pipeline,
        IReadOnlyList<IActionNode>? nodes = null,
        PreflightScope scope = PreflightScope.Pipeline,
        string? templateId = null,
        IReadOnlyDictionary<string, string>? triggerValues = null)
    {
        var runner = new PreflightRunner(_fs, FactsFor);

        return runner.Run(new PreflightRequest
        {
            Config = config,
            Pipeline = pipeline,
            Nodes = nodes ?? [],
            Scope = scope,
            TemplateId = templateId,
            TriggerValues = triggerValues,
            PipelineLockedBy = DescribeLockHolder(pipeline?.Tag),
            MinimumDiskGb = MinimumDiskGb,
        });
    }

    /// <summary>Who holds this pipeline's single-run lock, phrased for a refusal message.</summary>
    private string? DescribeLockHolder(string? pipelineTag)
    {
        if (string.IsNullOrWhiteSpace(pipelineTag)) return null;

        var held = _lockRegistry.Get(pipelineTag);
        if (held is null || held.Status != LockStatus.Active) return null;

        return $"{held.Owner.DisplayName} ({held.Owner.ClientKind})";
    }

    private PreflightAgentFacts FactsFor(string agentName)
    {
        var registered = _dispatcher.RegisteredAgents
            .Any(a => string.Equals(a, agentName, StringComparison.OrdinalIgnoreCase));

        if (!registered) return PreflightAgentFacts.Unknown(agentName);

        var health = _dispatcher.GetAgentHealth(agentName);
        var metrics = _telemetry?.GetCachedMetrics(agentName);
        var agentLock = _agentLocks.GetLock(agentName);

        return new PreflightAgentFacts
        {
            AgentName = agentName,
            IsRegistered = true,
            // No health record yet means the agent registered but has never answered - not online.
            IsOnline = health?.IsHealthy ?? false,
            IsBusy = _dispatcher.IsAgentExecuting(agentName),
            MaintenanceState = (_maintenance?.Get(agentName) ?? Maintenance.MaintenanceState.None).ToString(),
            LockedBy = agentLock is null ? null : $"{agentLock.WatchItemTag} ({agentLock.Source})",
            DiskFreeGb = metrics?.DiskFreeGb,
            RebootRequired = _updates?.Get(agentName)?.State == WindowsUpdateState.RebootRequired,
        };
    }
}
