using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

/// <summary>
/// Windows Update awareness for the Agents panel (spec R15–R20, R23): the severity banner stack, the notification
/// feed, the Maintenance-tab rollup and per-node rows. Reads <see cref="INodeUpdateStatusStore"/> and
/// <see cref="IFleetNotificationService"/>; both are optional so a host without the update stack degrades to empty.
/// </summary>
public partial class FleetUpdatesVM : ObservableObject, IDisposable
{
    private readonly INodeUpdateStatusStore? _store;
    private readonly IFleetNotificationService? _notifications;
    private readonly IFleetMaintenanceService? _maintenance;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly UpdatePolicyStore? _policy;
    private readonly Dispatcher _uiDispatcher;
    private bool _disposed;

    // Posture events arrive per node. At 50 agents on a 5-minute registry poll that is ~600 rebuilds/hour,
    // and each one re-scans every agent, so bursts are coalesced the same way FleetVM does. Notifications are
    // deliberately NOT debounced: that path is O(notifications), so delaying it would add latency for no gain.
    private readonly DispatcherTimer _rowsDebounce;

    /// <summary>One banner per severity, never per node (R15).</summary>
    public ObservableCollection<UpdateBannerVM> Banners { get; } = new();

    public ObservableCollection<FleetNotificationVM> Notifications { get; } = new();

    /// <summary>One row per registered agent, including nodes with nothing to report (R19).</summary>
    public ObservableCollection<NodeUpdateRowVM> Rows { get; } = new();

    /// <summary>What the Maintenance grid binds to: <see cref="Rows"/> narrowed by <see cref="RowFilter"/>.</summary>
    public ObservableCollection<NodeUpdateRowVM> FilteredRows { get; } = new();

    public IReadOnlyList<UpdateFilterOption> RowFilters { get; } = UpdateFilterOption.All;

    [ObservableProperty] private UpdateRowFilter _rowFilter = UpdateRowFilter.All;

    partial void OnRowFilterChanged(UpdateRowFilter value) => ApplyRowFilter();

    public string FilterSummary => FilteredRows.Count == Rows.Count
        ? $"{Rows.Count} agent(s)"
        : $"{FilteredRows.Count} of {Rows.Count} agent(s)";

    [ObservableProperty] private int _reportingCount;
    [ObservableProperty] private int _rebootRequiredCount;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _unreadCount;

    public bool HasBanners => Banners.Count > 0;
    public bool HasNotifications => Notifications.Count > 0;
    public bool HasUnread => UnreadCount > 0;
    public bool CanRebootAllReady => RebootRequiredCount > 0 && _maintenance is not null;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));
    partial void OnRebootRequiredCountChanged(int value) => OnPropertyChanged(nameof(CanRebootAllReady));

    /// <summary>Raised when a banner's Review action is used, so the shell can switch to the Maintenance tab.</summary>
    public event Action? ReviewRequested;

    /// <summary>Raised after <see cref="Rows"/> is rebuilt, so the fleet cards can re-apply their shield badges.</summary>
    public event Action? RowsChanged;

    public FleetUpdatesVM(
        IAgentGrpcDispatcher dispatcher,
        Dispatcher uiDispatcher,
        INodeUpdateStatusStore? store = null,
        IFleetNotificationService? notifications = null,
        IFleetMaintenanceService? maintenance = null,
        UpdatePolicyStore? policy = null)
    {
        _dispatcher = dispatcher;
        _uiDispatcher = uiDispatcher;
        _store = store;
        _notifications = notifications;
        _maintenance = maintenance;
        _policy = policy;

        if (_store is not null) _store.Changed += OnStatusChanged;
        if (_notifications is not null) _notifications.NotificationsChanged += OnNotificationsChanged;

        _rowsDebounce = new DispatcherTimer(DispatcherPriority.Background, uiDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _rowsDebounce.Tick += (_, _) =>
        {
            _rowsDebounce.Stop();
            if (_disposed) return;
            RebuildRows();
            RebuildBanners();
        };

        Refresh();
    }

    /// <summary>Coalesces a burst into one rebuild. Restarting the timer means only the last event does work.</summary>
    private void OnStatusChanged(object? sender, NodeUpdateStatusChanged e) =>
        _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _rowsDebounce.Stop();
            _rowsDebounce.Start();
        });

    private void OnNotificationsChanged(object? sender, EventArgs e) => _uiDispatcher.InvokeAsync(RefreshNotifications);

    [RelayCommand]
    private void Refresh()
    {
        RebuildRows();
        RebuildBanners();
        RefreshNotifications();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (_store is null) return;

        var statuses = _store.GetAll().ToDictionary(s => s.NodeId, StringComparer.OrdinalIgnoreCase);
        var staleAfter = TimeSpan.FromTicks((_policy?.Current.RegistryPollInterval ?? TimeSpan.FromMinutes(5)).Ticks * 2);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var built = new List<NodeUpdateRowVM>();

        foreach (var node in _dispatcher.RegisteredAgents)
        {
            statuses.TryGetValue(node, out var status);
            registered.Add(node);
            built.Add(new NodeUpdateRowVM(node, status, staleAfter));
        }

        // A node that reported but is no longer registered still matters to the operator. Membership is a set
        // lookup, not a scan of Rows per orphan.
        foreach (var orphan in statuses.Values.Where(s => !registered.Contains(s.NodeId)))
            built.Add(new NodeUpdateRowVM(orphan.NodeId, orphan, staleAfter));

        // Actionable first: at 50 rows the operator should not have to hunt for the node needing a reboot.
        foreach (var row in built
            .OrderBy(Severity)
            .ThenBy(r => r.AgentName, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(row);
        }

        ReportingCount = Rows.Count(r => r.HasReported);
        RebootRequiredCount = Rows.Count(r => r.State == WindowsUpdateState.RebootRequired);
        PendingCount = Rows.Count(r => r.State == WindowsUpdateState.UpdatePending);
        FailedCount = _notifications?.Notifications.Count(n => n.Kind == MaintenanceEventKind.UpdateFailed) ?? 0;

        ApplyRowFilter();
        RowsChanged?.Invoke();
    }

    private void ApplyRowFilter()
    {
        FilteredRows.Clear();
        foreach (var row in Rows.Where(Matches))
            FilteredRows.Add(row);
        OnPropertyChanged(nameof(FilterSummary));
    }

    private bool Matches(NodeUpdateRowVM row) => RowFilter switch
    {
        UpdateRowFilter.NeedsAttention => row.HasBadge || row.IsStale,
        UpdateRowFilter.RebootRequired => row.State == WindowsUpdateState.RebootRequired,
        UpdateRowFilter.UpdatesPending => row.State == WindowsUpdateState.UpdatePending,
        UpdateRowFilter.Stale => row.IsStale,
        _ => true,
    };

    // Reboot-required is the only state with an action attached, so it leads; never-reported nodes rank above
    // healthy ones because silence is a problem, not an all-clear.
    private static int Severity(NodeUpdateRowVM row) => row.State switch
    {
        WindowsUpdateState.RebootRequired => 0,
        WindowsUpdateState.UpdateInstalling => 1,
        WindowsUpdateState.UpdatePending => 2,
        WindowsUpdateState.Unknown => 3,
        WindowsUpdateState.Suppressed => 4,
        _ => 5,
    };

    private void RebuildBanners()
    {
        Banners.Clear();

        var rebooting = Rows.Where(r => r.State == WindowsUpdateState.RebootRequired).Select(r => r.AgentName).ToList();
        if (rebooting.Count > 0)
            Banners.Add(new UpdateBannerVM(
                IsWarning: true,
                Title: rebooting.Count == 1 ? "1 agent needs a reboot" : $"{rebooting.Count} agents need a reboot",
                Detail: $"{NameList(rebooting)} \u2014 no new work will be sent to them once their current run finishes."));

        var pending = Rows.Where(r => r.State == WindowsUpdateState.UpdatePending).Select(r => r.AgentName).ToList();
        if (pending.Count > 0)
            Banners.Add(new UpdateBannerVM(
                IsWarning: false,
                Title: pending.Count == 1 ? "1 agent has pending updates" : $"{pending.Count} agents have pending updates",
                Detail: $"{NameList(pending)} \u2014 they still accept work."));

        OnPropertyChanged(nameof(HasBanners));
    }

    // A banner names the affected agents, but at fleet scale the full list is an unreadable wall; the count is
    // already in the title, so the detail only needs enough names to recognise the group.
    private const int MaxNamesInBanner = 6;

    private static string NameList(IReadOnlyList<string> names) =>
        names.Count <= MaxNamesInBanner
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxNamesInBanner))} and {names.Count - MaxNamesInBanner} more";

    private void RefreshNotifications()
    {
        Notifications.Clear();
        if (_notifications is null)
        {
            UnreadCount = 0;
            OnPropertyChanged(nameof(HasNotifications));
            return;
        }

        foreach (var n in _notifications.Notifications)
            Notifications.Add(new FleetNotificationVM(n));

        UnreadCount = Notifications.Count(n => !n.IsMuted);
        FailedCount = _notifications.Notifications.Count(n => n.Kind == MaintenanceEventKind.UpdateFailed);
        OnPropertyChanged(nameof(HasNotifications));
    }

    [RelayCommand]
    private void Review() => ReviewRequested?.Invoke();

    [RelayCommand]
    private void MarkAllRead() => _notifications?.MarkAllRead();

    [RelayCommand]
    private void Acknowledge(Guid id) => _notifications?.Acknowledge(id);

    [RelayCommand]
    private void Snooze(string? nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
            _notifications?.Snooze(nodeId, TimeSpan.FromHours(4));
    }

    [RelayCommand]
    private async Task RebootNodeAsync(string? nodeId)
    {
        if (_maintenance is null || string.IsNullOrWhiteSpace(nodeId)) return;
        await StartRebootAsync(nodeId);
    }

    /// <summary>Patch Tuesday button: queues a reboot for every RebootRequired node that is not already draining (R19).</summary>
    [RelayCommand]
    private async Task RebootAllReadyAsync()
    {
        if (_maintenance is null) return;

        foreach (var row in Rows.Where(r => r.State == WindowsUpdateState.RebootRequired && !r.IsDraining).ToList())
            await StartRebootAsync(row.AgentName);
    }

    // Concurrency is capped inside FleetMaintenanceService; anything over the cap queues rather than failing.
    private async Task StartRebootAsync(string nodeId)
    {
        try
        {
            await _maintenance!.StartRebootAsync(
                new RebootRequest
                {
                    NodeId = nodeId,
                    TriggerSource = MaintenanceTriggerSource.FleetPanel,
                    Reason = "Windows Update requires a reboot",
                },
                CancellationToken.None);
        }
        catch (MaintenanceInProgressException)
        {
            // Node already has an operation in flight — nothing to do.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rowsDebounce.Stop();
        if (_store is not null) _store.Changed -= OnStatusChanged;
        if (_notifications is not null) _notifications.NotificationsChanged -= OnNotificationsChanged;
    }
}

/// <summary>One severity banner in the fleet banner stack (R15).</summary>
public sealed record UpdateBannerVM(bool IsWarning, string Title, string Detail);

/// <summary>Status filter for the Maintenance-tab updates grid.</summary>
public enum UpdateRowFilter { All, NeedsAttention, RebootRequired, UpdatesPending, Stale }

/// <summary>Filter choice plus its label, so the combo needs no enum-to-text converter.</summary>
public sealed record UpdateFilterOption(UpdateRowFilter Value, string Label)
{
    public static IReadOnlyList<UpdateFilterOption> All { get; } =
    [
        new(UpdateRowFilter.All, "All agents"),
        new(UpdateRowFilter.NeedsAttention, "Needs attention"),
        new(UpdateRowFilter.RebootRequired, "Reboot required"),
        new(UpdateRowFilter.UpdatesPending, "Updates pending"),
        new(UpdateRowFilter.Stale, "Not reporting"),
    ];
}

/// <summary>One entry in the notification flyout (R16).</summary>
public sealed class FleetNotificationVM
{
    public FleetNotificationVM(FleetNotification n)
    {
        Id = n.Id;
        NodeId = n.NodeId;
        Title = n.Title;
        Description = n.Description;
        Acknowledged = n.Acknowledged;
        IsMuted = n.IsMutedAt(DateTimeOffset.UtcNow);
        Kind = n.Kind;
        SourceText = UpdateDisplay.SourceText(n.Source);
        RelativeTime = UpdateDisplay.Relative(n.DetectedUtc);
    }

    public Guid Id { get; }
    public string NodeId { get; }
    public string Title { get; }
    public string Description { get; }
    public bool Acknowledged { get; }

    /// <summary>Acknowledged, or inside an unexpired snooze. Snapshotted at construction, so the badge
    /// picks up a lapsed snooze on the next notification refresh rather than the instant it expires.</summary>
    public bool IsMuted { get; }

    public MaintenanceEventKind Kind { get; }
    public string SourceText { get; }
    public string RelativeTime { get; }
}

/// <summary>One row of the Maintenance-tab updates grid, and the backing data for a card's shield badge (R17/R19/R23).</summary>
public sealed class NodeUpdateRowVM
{
    public NodeUpdateRowVM(string agentName, NodeUpdateStatus? status, TimeSpan staleAfter)
    {
        AgentName = agentName;
        HasReported = status is not null;
        State = status?.State ?? WindowsUpdateState.Unknown;
        PendingCount = status?.PendingCount ?? 0;
        Items = status?.Items ?? [];
        IsDraining = status?.State == WindowsUpdateState.RebootRequired;

        StateText = UpdateDisplay.StateText(State, HasReported);
        DetectedBy = status is null ? "—" : UpdateDisplay.SourceText(status.LastSource);
        LastInstall = status?.LastInstallUtc is { } li ? li.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—";
        LastReport = status is null ? "never" : UpdateDisplay.Relative(status.LastReportUtc);

        // "No alert" and "agent has not reported in six hours" must not look the same.
        IsStale = status is null || DateTimeOffset.UtcNow - status.LastReportUtc > staleAfter;

        DispatchText = State switch
        {
            WindowsUpdateState.RebootRequired => "Draining",
            WindowsUpdateState.UpdateInstalling => "Blocked",
            WindowsUpdateState.Suppressed => "Suppressed",
            _ => "Eligible",
        };

        Provenance = status is null
            ? "No report received from this agent yet."
            : $"Detected by {DetectedBy} at {status.LastEventUtc?.LocalDateTime:yyyy-MM-dd HH:mm} · last agent report {LastReport}";
    }

    public string AgentName { get; }
    public bool HasReported { get; }
    public WindowsUpdateState State { get; }
    public string StateText { get; }
    public int PendingCount { get; }
    public IReadOnlyList<UpdateItemDto> Items { get; }
    public string DetectedBy { get; }
    public string LastInstall { get; }
    public string LastReport { get; }
    public bool IsStale { get; }
    public bool IsDraining { get; }
    public string DispatchText { get; }
    public string Provenance { get; }

    public bool HasBadge => State is WindowsUpdateState.UpdatePending or WindowsUpdateState.RebootRequired;
    public bool IsRebootRequired => State == WindowsUpdateState.RebootRequired;
    public string BadgeText => State switch
    {
        WindowsUpdateState.RebootRequired => "Reboot required",
        WindowsUpdateState.UpdatePending => $"{PendingCount} pending",
        _ => "",
    };
}
internal static class UpdateDisplay
{
    public static string StateText(WindowsUpdateState state, bool hasReported) => state switch
    {
        WindowsUpdateState.UpToDate => "Up to date",
        WindowsUpdateState.UpdatePending => "Updates pending",
        WindowsUpdateState.UpdateInstalling => "Installing",
        WindowsUpdateState.RebootRequired => "Reboot required",
        WindowsUpdateState.Suppressed => "Suppressed",
        _ => hasReported ? "Unknown" : "Not reported",
    };

    public static string SourceText(MaintenanceEventSource source) => source switch
    {
        MaintenanceEventSource.EventLog => "Event log",
        MaintenanceEventSource.RegistryPoll => "Registry poll",
        MaintenanceEventSource.StartupSnapshot => "Startup snapshot",
        _ => "—",
    };

    public static string Relative(DateTimeOffset utc)
    {
        var delta = DateTimeOffset.UtcNow - utc;
        if (delta < TimeSpan.Zero) return "just now";
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
        return $"{(int)delta.TotalDays} d ago";
    }
}
