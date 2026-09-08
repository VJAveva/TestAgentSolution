using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class FleetVM : ObservableObject, IDisposable
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IFleetMaintenanceService? _maintenanceService;
    private readonly IMaintenanceStateStore? _maintenanceState;
    private readonly Dispatcher _uiDispatcher;
    private bool _disposed;

    /// <summary>Grouped agent collection for the fleet panel.</summary>
    public ObservableCollection<FleetGroupVM> Groups { get; } = new();

    /// <summary>Flat list retained for backward compat (e.g. tests, selection).</summary>
    public ObservableCollection<FleetCardVM> Cards { get; } = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _busyCount;
    [ObservableProperty] private int _freeCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _revertingCount;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _utilizationBarWidth;

    public bool HasReverting => RevertingCount > 0;
    partial void OnRevertingCountChanged(int value) => OnPropertyChanged(nameof(HasReverting));

    public event Action<string>? AgentSelected;
    public event Action? RegisterAgentClicked;

    /// <summary>Windows Update posture source for the card shield badges (R17); null until attached.</summary>
    public FleetUpdatesVM? Updates { get; private set; }

    /// <summary>Wires the shared update view model so card badges track posture changes.</summary>
    public void AttachUpdates(FleetUpdatesVM updates)
    {
        Updates = updates;
        updates.RowsChanged += ApplyUpdateBadges;
        OnPropertyChanged(nameof(Updates));
        ApplyUpdateBadges();
    }

    private void ApplyUpdateBadges()
    {
        if (Updates is null) return;

        foreach (var card in Cards)
        {
            var row = Updates.Rows.FirstOrDefault(
                r => string.Equals(r.AgentName, card.AgentName, StringComparison.OrdinalIgnoreCase));

            card.HasUpdateBadge = row?.HasBadge ?? false;
            card.IsRebootRequired = row?.IsRebootRequired ?? false;
            card.UpdateBadgeText = row?.BadgeText ?? "";
            card.UpdateTooltip = row?.Provenance ?? "";
            card.IsDraining = _maintenanceState?.Get(card.AgentName) == MaintenanceState.Draining;
        }
    }

    private readonly DispatcherTimer _healthTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private bool _isProbing;

    /// <summary>Debounce timer: coalesces rapid event bursts into a single Refresh.</summary>
    private readonly DispatcherTimer _refreshDebounce;

    public FleetVM(
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IEventAggregator events,
        Dispatcher uiDispatcher,
        IFleetMaintenanceService? maintenanceService = null,
        IMaintenanceStateStore? maintenanceState = null)
    {
        _dispatcher = dispatcher;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
        _maintenanceService = maintenanceService;
        _maintenanceState = maintenanceState;
        _uiDispatcher = uiDispatcher;

        if (_maintenanceService is not null)
        {
            _maintenanceService.ProgressChanged += OnMaintenanceProgress;
            _maintenanceService.OperationCompleted += OnMaintenanceCompleted;
        }

        // Scale fix: Debounce all event-driven refreshes to prevent UI starvation.
        // At 200 agents with heartbeats every 5s, up to 40 events/second can arrive.
        // Without debounce, Refresh() is called for each one (full Cards.Clear + rebuild).
        _refreshDebounce = new DispatcherTimer(DispatcherPriority.Background, uiDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };

        events.Subscribe<AgentLocksChangedEvent>(_ => ScheduleRefresh());
        events.Subscribe<ExecutionStartedEvent>(_ => ScheduleRefresh());
        events.Subscribe<ExecutionCompletedEvent>(_ => ScheduleRefresh());
        events.Subscribe<AgentHeartbeatEvent>(_ => ScheduleRefresh());
        events.Subscribe<AgentRegisteredEvent>(_ => ScheduleRefresh());
        events.Subscribe<AgentUnregisteredEvent>(_ => ScheduleRefresh());
        events.Subscribe<NodeProgressEvent>(_ => ScheduleRefresh());

        // 5-second periodic health probe to detect power cycle recovery
        _healthTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            async (_, _) => await ProbeHealthAsync(),
            uiDispatcher);
        _healthTimer.Start();

        // 1-second timer to advance the elapsed clock on cards under maintenance.
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickElapsed(), uiDispatcher);
        _elapsedTimer.Start();

        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Stop();
        _elapsedTimer.Stop();
        _refreshDebounce.Stop();
        if (_maintenanceService is not null)
        {
            _maintenanceService.ProgressChanged -= OnMaintenanceProgress;
            _maintenanceService.OperationCompleted -= OnMaintenanceCompleted;
        }
    }

    /// <summary>
    /// Coalesces multiple event-driven refresh requests within 250ms into one Refresh() call.
    /// Safe to call from any thread — marshals to UI dispatcher.
    /// </summary>
    private void ScheduleRefresh()
    {
        _uiDispatcher.InvokeAsync(() =>
        {
            // Restart the timer each time — only the LAST event in a burst triggers Refresh.
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

    /// <summary>
    /// Probes all registered agents every 5 seconds.
    /// Updates health state so Fleet cards reflect actual status after power cycles.
    /// Skips agents currently executing (locked) to avoid unnecessary gRPC calls.
    /// </summary>
    private async Task ProbeHealthAsync()
    {
        // Prevent overlapping probes when agents are slow/timing out
        if (_isProbing) return;
        _isProbing = true;
        try
        {
            var agents = _dispatcher.RegisteredAgents.ToList();
            var allLocks = _lockManager.GetAllLocks();
            bool changed = false;

            foreach (var name in agents)
            {
                // Skip agents that are actively executing — they're known busy
                if (allLocks.Any(l => string.Equals(l.AgentName, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var health = _dispatcher.GetAgentHealth(name);
                if (health == null) continue;

                var wasHealthy = health.IsHealthy;
                await _dispatcher.TestConnectionAsync(name);
                if (wasHealthy != health.IsHealthy)
                    changed = true;
            }

            if (changed)
                Refresh();
        }
        finally
        {
            _isProbing = false;
        }
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    public void Refresh()
    {
        using var _perf = Diagnostics.UiPerfDiagnostics.Measure("FleetVM.Refresh");
        var agents = _dispatcher.RegisteredAgents.ToList();
        var allHealth = _dispatcher.GetAllAgentHealth();
        var allLocks = _lockManager.GetAllLocks();

        int busy = 0, free = 0, offline = 0, failed = 0;

        // Track which agents are still present so we can remove stale cards
        var activeAgentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var agentName in agents)
        {
            // Apply filter
            if (!string.IsNullOrEmpty(FilterText) &&
                !agentName.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                continue;

            activeAgentNames.Add(agentName);

            // Find existing card or create new one (in-place mutation preserves focus)
            var card = Cards.FirstOrDefault(c =>
                string.Equals(c.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
            bool isNew = card == null;
            card ??= new FleetCardVM { AgentName = agentName };

            var address = _dispatcher.GetAgentAddress(agentName) ?? "";
            card.Address = address;

            allHealth.TryGetValue(agentName, out var health);
            var agentLock = allLocks.FirstOrDefault(l =>
                string.Equals(l.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

            if (agentLock != null)
            {
                // Lock check takes priority — agent is executing work
                card.SessionId = agentLock.SessionId;
                card.GroupKey = agentLock.SessionId;
                card.WatchItemTag = agentLock.WatchItemTag;
                var session = _sessionManager.GetSession(agentLock.SessionId);
                // Prefer the friendly attributed user from the session; fall back to the
                // raw agent-lock identity (e.g. "WPF/user@machine") when not captured.
                card.Owner =
                    !string.IsNullOrWhiteSpace(session?.UserDisplayName) ? session!.UserDisplayName
                    : !string.IsNullOrWhiteSpace(session?.UserId) ? session!.UserId
                    : agentLock.UserId;
                card.OwnerRole = session?.UserRole ?? "";
                var agentSummary = session?.GetAgentSummaries()
                    .FirstOrDefault(s => string.Equals(s.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

                if (agentSummary != null && agentSummary.Status == "Failed")
                {
                    card.Status = "Failed";
                    card.StatusDetail = $"{agentSummary.CompletedCount}/{agentSummary.TotalCount} — has failures";
                    card.IsError = true;
                    failed++;
                }
                else
                {
                    card.Status = "Busy";
                    card.StatusDetail = agentSummary != null
                        ? $"{agentSummary.CompletedCount}/{agentSummary.TotalCount} actions"
                        : agentLock.WatchItemTag;
                    card.IsError = false;
                    busy++;
                }

                // Populate current action and progress for pill display
                if (agentSummary != null)
                {
                    var running = agentSummary.Actions
                        .FirstOrDefault(a => a.Outcome == ActionOutcome.Unknown);
                    if (running != null)
                        card.CurrentActionTag = running.ActionTag;
                    if (agentSummary.TotalCount > 0)
                        card.ProgressPercent = (int)(100.0 * agentSummary.CompletedCount / agentSummary.TotalCount);
                }
                else
                {
                    card.CurrentActionTag = "";
                    card.ProgressPercent = -1;
                }
            }
            else if (health != null && !health.IsHealthy)
            {
                card.Status = "Offline";
                card.StatusDetail = health.ConsecutiveFailures > 0
                    ? $"{health.ConsecutiveFailures} failures"
                    : "Unreachable";
                card.IsError = true;
                card.SessionId = "";
                card.GroupKey = "";
                card.Owner = "";
                card.OwnerRole = "";
                card.WatchItemTag = "";
                card.CurrentActionTag = "";
                card.ProgressPercent = -1;
                offline++;
            }
            else
            {
                card.Status = "Free";
                card.StatusDetail = "Idle";
                card.IsError = false;
                card.SessionId = "";
                card.GroupKey = "";
                card.Owner = "";
                card.OwnerRole = "";
                card.WatchItemTag = "";
                card.CurrentActionTag = "";
                card.ProgressPercent = -1;
                free++;
            }

            // Reflect authoritative maintenance state (the revert engine owns it) on the card.
            if (_maintenanceState is not null)
            {
                var mstate = _maintenanceState.Get(agentName);
                card.IsUnderMaintenance = mstate is MaintenanceState.Reverting or MaintenanceState.Rebooting or MaintenanceState.Updating;
                card.IsQuarantined = mstate == MaintenanceState.Quarantined;
                card.MaintenanceStateText = mstate == MaintenanceState.None ? "" : mstate.ToString();
            }

            if (isNew)
                Cards.Add(card);
        }

        // Remove cards for agents that are no longer registered or filtered out
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!activeAgentNames.Contains(Cards[i].AgentName))
                Cards.RemoveAt(i);
        }

        // Rebuild groups (lightweight — groups don't hold focus state)
        Groups.Clear();
        var assigned = Cards
            .Where(c => !string.IsNullOrEmpty(c.GroupKey))
            .GroupBy(c => c.GroupKey)
            .Select(g => new FleetGroupVM(
                groupKey: g.Key,
                title: g.First().WatchItemTag ?? g.Key,
                owner: g.First().Owner,
                ownerRole: g.First().OwnerRole,
                isAvailablePool: false,
                agents: g.OrderBy(c => c.AgentName).ToList()))
            .OrderBy(g => g.Title)
            .ToList();

        var availableCards = Cards
            .Where(c => string.IsNullOrEmpty(c.GroupKey))
            .OrderBy(c => c.AgentName)
            .ToList();

        var availableGroup = new FleetGroupVM(
            groupKey: "Available",
            title: "Available",
            owner: null,
            ownerRole: null,
            isAvailablePool: true,
            agents: availableCards);

        foreach (var g in assigned)
            Groups.Add(g);
        Groups.Add(availableGroup);

        TotalCount = Cards.Count;
        BusyCount = busy;
        FreeCount = free;
        OfflineCount = offline;
        FailedCount = failed;
        IsEmpty = Cards.Count == 0;

        // Utilization bar: proportional width (max 80px) based on busy/total
        UtilizationBarWidth = Cards.Count > 0
            ? (int)(80.0 * busy / Cards.Count)
            : 0;

        RevertingCount = Cards.Count(c => c.IsUnderMaintenance);
        ApplyUpdateBadges();
    }

    [RelayCommand]
    private void SelectAgent(FleetCardVM? card)
    {
        if (card != null)
            AgentSelected?.Invoke(card.AgentName);
    }

    [RelayCommand]
    private void RequestRegisterAgent() => RegisterAgentClicked?.Invoke();

    // ── Fleet maintenance (revert) ──

    [RelayCommand]
    private void Revert(FleetCardVM? card)
    {
        if (card is null || _maintenanceService is null) return;
        var dialog = new RevertMachineDialog();
        dialog.Initialize(card.AgentName);
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task Reboot(FleetCardVM? card)
    {
        if (card is null || _maintenanceService is null) return;
        var confirm = ThemedMessageBox.Show(
            $"Reboot '{card.AgentName}'?\n\nAny running test on it will be interrupted.",
            "Reboot machine", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var request = new RebootRequest
        {
            NodeId = card.AgentName,
            TriggerSource = MaintenanceTriggerSource.FleetPanel,
            TriggeredBy = Environment.UserName,
        };
        try { await _maintenanceService.StartRebootAsync(request, CancellationToken.None); }
        catch (MaintenanceInProgressException) { /* already under maintenance */ }
    }

    [RelayCommand]
    private async Task CancelRevert(FleetCardVM? card)
    {
        if (card is null || _maintenanceService is null || card.CurrentOperationId == Guid.Empty) return;
        await _maintenanceService.RequestCancelAsync(card.CurrentOperationId);
    }

    [RelayCommand]
    private async Task ClearQuarantine(FleetCardVM? card)
    {
        if (card is null || _maintenanceService is null) return;
        await _maintenanceService.ClearQuarantineAsync(card.AgentName, Environment.UserName);
        _uiDispatcher.InvokeAsync(Refresh);
    }

    [RelayCommand]
    private void OpenRemoteDesktop(FleetCardVM? card)
    {
        if (card is null) return;
        var host = ResolveHost(card);
        if (string.IsNullOrWhiteSpace(host)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("mstsc.exe", $"/v:{host}") { UseShellExecute = true });
        }
        catch
        {
            // mstsc unavailable or launch blocked — nothing actionable from the fleet panel.
        }
    }

    private static string ResolveHost(FleetCardVM card)
        => !string.IsNullOrWhiteSpace(card.Address) && Uri.TryCreate(card.Address, UriKind.Absolute, out var uri)
            ? uri.Host
            : card.AgentName;

    private void TickElapsed()
    {
        foreach (var card in Cards)
        {
            if (card.IsUnderMaintenance && card.MaintenanceStartedUtc != default)
                card.MaintenanceElapsed = FormatElapsed(DateTime.UtcNow - card.MaintenanceStartedUtc);
        }
    }

    private static string FormatElapsed(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void OnMaintenanceProgress(object? sender, MaintenanceProgress p)
    {
        _uiDispatcher.InvokeAsync(() =>
        {
            var card = Cards.FirstOrDefault(c =>
                string.Equals(c.AgentName, p.NodeId, StringComparison.OrdinalIgnoreCase));
            if (card is null) return;
            if (!card.IsUnderMaintenance)
                card.MaintenanceStartedUtc = DateTime.UtcNow - p.Elapsed;
            card.IsUnderMaintenance = true;
            card.IsQuarantined = false;
            card.CurrentOperationId = p.OperationId;
            card.MaintenanceStep = p.StepNumber;
            card.MaintenanceStepCount = p.StepCount;
            card.MaintenancePhase = p.Message;
            card.MaintenanceStateText = _maintenanceState?.Get(p.NodeId).ToString() ?? "Reverting";
            RevertingCount = Cards.Count(c => c.IsUnderMaintenance);
        });
    }

    private void OnMaintenanceCompleted(object? sender, MaintenanceOperation op)
    {
        _uiDispatcher.InvokeAsync(() =>
        {
            var card = Cards.FirstOrDefault(c =>
                string.Equals(c.AgentName, op.NodeId, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                card.MaintenancePhase = op.State.ToString();
                card.MaintenanceStep = 0;
                card.MaintenanceStepCount = 0;
                card.CurrentOperationId = Guid.Empty;
                card.MaintenanceStartedUtc = default;
                card.MaintenanceElapsed = "";
            }
            Refresh();
        });
    }
}

/// <summary>
/// Represents a group of agents sharing the same session assignment
/// (or the "Available" idle pool).
/// </summary>
public partial class FleetGroupVM : ObservableObject
{
    public FleetGroupVM(string groupKey, string title, string? owner,
        string? ownerRole, bool isAvailablePool, IReadOnlyList<FleetCardVM> agents)
    {
        GroupKey = groupKey;
        Title = title;
        Owner = owner;
        OwnerRole = ownerRole;
        IsAvailablePool = isAvailablePool;
        Agents = new ObservableCollection<FleetCardVM>(agents);
    }

    public string GroupKey { get; }
    public string Title { get; }
    public string? Owner { get; }
    public string? OwnerRole { get; }
    public bool IsAvailablePool { get; }
    public ObservableCollection<FleetCardVM> Agents { get; }

    public int Count => Agents.Count;
    public string OwnerDisplay => string.IsNullOrEmpty(Owner) ? "" : $"({Owner})";
    /// <summary>Whether a triggering-user role was captured (drives role-glyph visibility).</summary>
    public bool HasOwnerRole => !string.IsNullOrWhiteSpace(OwnerRole);
    /// <summary>Emoji glyph for the triggering user's role.</summary>
    public string OwnerRoleIcon => RoleGlyph.Icon(OwnerRole);
    /// <summary>Short label for the triggering user's role.</summary>
    public string OwnerRoleLabel => RoleGlyph.Label(OwnerRole);
    /// <summary>Hover text attributing the running pipeline to a user and role.</summary>
    public string OwnerTooltip =>
        string.IsNullOrEmpty(Owner) ? ""
        : HasOwnerRole ? $"Triggered by {Owner} ({OwnerRoleLabel})"
        : $"Triggered by {Owner}";
    public bool HasAgents => Agents.Count > 0;
    public string AccentKind => IsAvailablePool ? "Idle" : "Running";
}

public partial class FleetCardVM : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _status = "Free";
    [ObservableProperty] private string _statusDetail = "Idle";
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _groupKey = "";
    [ObservableProperty] private string _owner = "";
    [ObservableProperty] private string _ownerRole = "";
    [ObservableProperty] private string _watchItemTag = "";
    [ObservableProperty] private string _currentActionTag = "";
    [ObservableProperty] private int _progressPercent = -1;  // -1 = unknown
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private int _latencyMs = -1;  // -1 = not measured

    /// <summary>Pill display: "Tag → Action 50%" or "Tag" if no action info.</summary>
    public string PipelinePillText
    {
        get
        {
            if (string.IsNullOrEmpty(WatchItemTag)) return "";
            var text = WatchItemTag;
            if (!string.IsNullOrEmpty(CurrentActionTag))
                text += $" \u2192 {CurrentActionTag}";
            if (ProgressPercent >= 0)
                text += $" {ProgressPercent}%";
            return text;
        }
    }

    partial void OnWatchItemTagChanged(string value) => OnPropertyChanged(nameof(PipelinePillText));
    partial void OnCurrentActionTagChanged(string value) => OnPropertyChanged(nameof(PipelinePillText));
    partial void OnProgressPercentChanged(int value) => OnPropertyChanged(nameof(PipelinePillText));

    // ── Fleet maintenance (revert) live state ──
    [ObservableProperty] private bool _isUnderMaintenance;
    [ObservableProperty] private bool _isQuarantined;
    [ObservableProperty] private string _maintenanceStateText = "";
    [ObservableProperty] private string _maintenancePhase = "";
    [ObservableProperty] private int _maintenanceStep;
    [ObservableProperty] private int _maintenanceStepCount;
    [ObservableProperty] private Guid _currentOperationId;
    [ObservableProperty] private DateTime _maintenanceStartedUtc;
    [ObservableProperty] private string _maintenanceElapsed = "";

    public int MaintenanceProgressPercent =>
        MaintenanceStepCount > 0 ? (int)(100.0 * MaintenanceStep / MaintenanceStepCount) : 0;

    public string MaintenanceLine
    {
        get
        {
            if (MaintenanceStepCount <= 0) return MaintenancePhase;
            var elapsed = string.IsNullOrEmpty(MaintenanceElapsed) ? "" : $"{MaintenanceElapsed} \u00b7 ";
            return $"{MaintenanceStateText} \u00b7 {MaintenanceStep}/{MaintenanceStepCount} \u00b7 {elapsed}{MaintenancePhase}";
        }
    }

    partial void OnMaintenanceStepChanged(int value)
    {
        OnPropertyChanged(nameof(MaintenanceProgressPercent));
        OnPropertyChanged(nameof(MaintenanceLine));
    }
    partial void OnMaintenanceStepCountChanged(int value)
    {
        OnPropertyChanged(nameof(MaintenanceProgressPercent));
        OnPropertyChanged(nameof(MaintenanceLine));
    }
    partial void OnMaintenancePhaseChanged(string value) => OnPropertyChanged(nameof(MaintenanceLine));

    // ── Windows Update shield badge (spec R17) ──
    [ObservableProperty] private bool _hasUpdateBadge;
    [ObservableProperty] private bool _isRebootRequired;
    [ObservableProperty] private bool _isDraining;
    [ObservableProperty] private string _updateBadgeText = "";
    [ObservableProperty] private string _updateTooltip = "";
    partial void OnMaintenanceStateTextChanged(string value) => OnPropertyChanged(nameof(MaintenanceLine));
    partial void OnMaintenanceElapsedChanged(string value) => OnPropertyChanged(nameof(MaintenanceLine));
}
