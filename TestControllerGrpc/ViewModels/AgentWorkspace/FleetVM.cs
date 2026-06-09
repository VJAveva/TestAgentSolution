using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class FleetVM : ObservableObject, IDisposable
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
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
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _utilizationBarWidth;

    public event Action<string>? AgentSelected;
    public event Action? RegisterAgentClicked;

    private readonly DispatcherTimer _healthTimer;
    private bool _isProbing;

    /// <summary>Debounce timer: coalesces rapid event bursts into a single Refresh.</summary>
    private readonly DispatcherTimer _refreshDebounce;

    public FleetVM(
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager,
        IEventAggregator events,
        Dispatcher uiDispatcher)
    {
        _dispatcher = dispatcher;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
        _uiDispatcher = uiDispatcher;

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

        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Stop();
        _refreshDebounce.Stop();
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
                card.Owner = agentLock.UserId;
                card.WatchItemTag = agentLock.WatchItemTag;
                var session = _sessionManager.GetSession(agentLock.SessionId);
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
                card.WatchItemTag = "";
                card.CurrentActionTag = "";
                card.ProgressPercent = -1;
                free++;
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
    }

    [RelayCommand]
    private void SelectAgent(FleetCardVM? card)
    {
        if (card != null)
            AgentSelected?.Invoke(card.AgentName);
    }

    [RelayCommand]
    private void RequestRegisterAgent() => RegisterAgentClicked?.Invoke();
}

/// <summary>
/// Represents a group of agents sharing the same session assignment
/// (or the "Available" idle pool).
/// </summary>
public partial class FleetGroupVM : ObservableObject
{
    public FleetGroupVM(string groupKey, string title, string? owner,
        bool isAvailablePool, IReadOnlyList<FleetCardVM> agents)
    {
        GroupKey = groupKey;
        Title = title;
        Owner = owner;
        IsAvailablePool = isAvailablePool;
        Agents = new ObservableCollection<FleetCardVM>(agents);
    }

    public string GroupKey { get; }
    public string Title { get; }
    public string? Owner { get; }
    public bool IsAvailablePool { get; }
    public ObservableCollection<FleetCardVM> Agents { get; }

    public int Count => Agents.Count;
    public string OwnerDisplay => string.IsNullOrEmpty(Owner) ? "" : $"({Owner})";
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
}
