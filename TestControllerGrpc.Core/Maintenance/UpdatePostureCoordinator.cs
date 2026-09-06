namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Turns a node's reported Windows Update posture into a scheduling decision (spec §14/§16, Prompt 16/17).
/// Subscribes to <see cref="INodeUpdateStatusStore.Changed"/>, runs the <see cref="IUpdatePolicyEvaluator"/>, and
/// writes the result through <see cref="IMaintenanceStateStore"/> — the same authority the revert engine writes, so
/// the dispatch gate sees update-driven holds exactly like maintenance-driven ones.
/// </summary>
public sealed class UpdatePostureCoordinator : IDisposable
{
    private readonly INodeUpdateStatusStore _statuses;
    private readonly IUpdatePolicyEvaluator _evaluator;
    private readonly IMaintenanceStateStore _maintenance;
    private readonly IFleetNotificationService _notifications;
    private readonly IUpdatePolicyStore _policy;
    private bool _subscribed;

    public UpdatePostureCoordinator(
        INodeUpdateStatusStore statuses,
        IUpdatePolicyEvaluator evaluator,
        IMaintenanceStateStore maintenance,
        IFleetNotificationService notifications,
        IUpdatePolicyStore policy)
    {
        _statuses = statuses;
        _evaluator = evaluator;
        _maintenance = maintenance;
        _notifications = notifications;
        _policy = policy;
    }

    public void Start()
    {
        if (_subscribed) return;
        _statuses.Changed += OnStatusChanged;
        _subscribed = true;
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _statuses.Changed -= OnStatusChanged;
        _subscribed = false;
    }

    private void OnStatusChanged(object? sender, NodeUpdateStatusChanged e) => Apply(e.NodeId);

    /// <summary>Re-evaluate one node and apply the resulting state. Safe to call directly (tests, startup replay).</summary>
    public void Apply(string nodeId)
    {
        var status = _statuses.Get(nodeId);
        if (status is null) return;

        ApplyState(nodeId, _evaluator.Evaluate(status, _policy.Current));
        Notify(status);
    }

    /// <summary>
    /// Called when a node finishes its last in-flight run. A Draining node then moves to the state its posture
    /// really implies — normally <see cref="MaintenanceState.Updating"/> for a pending reboot — and the operator is
    /// told it is ready to reboot (spec §14, Prompt 17).
    /// </summary>
    public void OnNodeIdle(string nodeId)
    {
        if (_maintenance.Get(nodeId) != MaintenanceState.Draining) return;

        var status = _statuses.Get(nodeId);
        if (status is null) return;

        if (status.State != WindowsUpdateState.RebootRequired)
        {
            _maintenance.Set(nodeId, MaintenanceState.None);
            return;
        }

        _maintenance.Set(nodeId, MaintenanceState.Updating);
        _notifications.Raise(new FleetNotification
        {
            NodeId = nodeId,
            Kind = MaintenanceEventKind.RebootRequired,
            Title = $"{nodeId} is ready to reboot",
            Description = "The node finished its last run and is now held out of rotation awaiting a reboot.",
            Source = status.LastSource,
        });

        TryAutoReboot(nodeId);
    }

    /// <summary>Supplies the reboot front door; left unset the coordinator only ever notifies.</summary>
    public IFleetMaintenanceService? FleetMaintenance { get; set; }

    // Unattended reboot is opt-in and window-bound. Outside the window the node simply waits, already blocked.
    private void TryAutoReboot(string nodeId)
    {
        var policy = _policy.Current;
        if (!policy.AutoReboot || FleetMaintenance is null) return;
        if (!policy.IsWithinAutoRebootWindow(DateTimeOffset.UtcNow)) return;

        try
        {
            // Concurrency is capped inside FleetMaintenanceService, which queues rather than rejects.
            _ = FleetMaintenance.StartRebootAsync(
                new RebootRequest
                {
                    NodeId = nodeId,
                    TriggerSource = MaintenanceTriggerSource.Autopilot,
                    TriggeredBy = "WindowsUpdatePolicy",
                    Reason = "Automatic reboot after Windows Update",
                },
                CancellationToken.None);
        }
        catch (MaintenanceInProgressException)
        {
            // Something else already owns this node; the posture stays blocked and will be retried next transition.
        }
    }

    /// <summary>Opens the post-maintenance suppression window so revert/reboot churn does not re-alarm (spec §15).</summary>
    public void OnMaintenanceCompleted(string nodeId)
        => _statuses.Suppress(nodeId, DateTimeOffset.UtcNow.Add(_policy.Current.SuppressionWindow));

    private void ApplyState(string nodeId, MaintenanceState desired)
    {
        var current = _maintenance.Get(nodeId);

        // Operator- and engine-owned states outrank update posture; never steal a node mid-operation.
        if (current is MaintenanceState.Reverting or MaintenanceState.Rebooting or MaintenanceState.Quarantined)
            return;

        // A node already blocked for updates must not fall back to Draining on a repeat report of the same posture.
        if (current == MaintenanceState.Updating && desired == MaintenanceState.Draining)
            return;

        if (current != desired)
            _maintenance.Set(nodeId, desired);
    }

    private void Notify(NodeUpdateStatus status)
    {
        if (status.State is WindowsUpdateState.Unknown) return;

        var (kind, title, description) = Describe(status);

        _notifications.Raise(new FleetNotification
        {
            NodeId = status.NodeId,
            Kind = kind,
            Title = title,
            Description = description,
            Source = status.LastSource,
            DetectedUtc = status.LastEventUtc ?? status.LastReportUtc,
            IsSuppressed = status.State == WindowsUpdateState.Suppressed,
        });
    }

    private static (MaintenanceEventKind Kind, string Title, string Description) Describe(NodeUpdateStatus s) =>
        s.State switch
        {
            WindowsUpdateState.RebootRequired => (
                MaintenanceEventKind.RebootRequired,
                $"{s.NodeId} needs a reboot",
                "No new work will be sent to this node once its current run finishes."),
            WindowsUpdateState.UpdatePending => (
                MaintenanceEventKind.UpdatePending,
                $"{s.NodeId} has {s.PendingCount} pending update(s)",
                "The node still accepts work."),
            WindowsUpdateState.UpdateInstalling => (
                MaintenanceEventKind.UpdateInstalling,
                $"{s.NodeId} is installing updates",
                "The node is held out of rotation while Windows Update runs."),
            WindowsUpdateState.UpToDate => (
                MaintenanceEventKind.RebootCleared,
                $"{s.NodeId} is up to date",
                "The node is back in rotation."),
            WindowsUpdateState.Suppressed => (
                MaintenanceEventKind.Unspecified,
                $"{s.NodeId} update alerts suppressed",
                "Recent maintenance on this node — update events are recorded but not alarmed."),
            _ => (MaintenanceEventKind.Unspecified, $"{s.NodeId} update posture changed", ""),
        };
}
