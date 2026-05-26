using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        events.Subscribe<AgentLocksChangedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<ExecutionStartedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<ExecutionCompletedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<AgentHeartbeatEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<AgentRegisteredEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<AgentUnregisteredEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<NodeProgressEvent>(_ => uiDispatcher.InvokeAsync(Refresh));

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

        Cards.Clear();
        Groups.Clear();
        int busy = 0, free = 0, offline = 0, failed = 0;

        // Build all cards first
        var allCards = new List<FleetCardVM>();
        foreach (var agentName in agents)
        {
            // Apply filter
            if (!string.IsNullOrEmpty(FilterText) &&
                !agentName.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                continue;

            var card = new FleetCardVM { AgentName = agentName };
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
                    busy++;
                }
            }
            else if (health != null && !health.IsHealthy)
            {
                card.Status = "Offline";
                card.StatusDetail = health.ConsecutiveFailures > 0
                    ? $"{health.ConsecutiveFailures} failures"
                    : "Unreachable";
                card.IsError = true;
                offline++;
            }
            else
            {
                card.Status = "Free";
                card.StatusDetail = "Idle";
                free++;
            }

            allCards.Add(card);
            Cards.Add(card);
        }

        // Build groups: assigned groups (by session) + Available pool
        var assigned = allCards
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

        var availableCards = allCards
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

        TotalCount = allCards.Count;
        BusyCount = busy;
        FreeCount = free;
        OfflineCount = offline;
        FailedCount = failed;
        IsEmpty = allCards.Count == 0;

        // Utilization bar: proportional width (max 80px) based on busy/total
        UtilizationBarWidth = allCards.Count > 0
            ? (int)(80.0 * busy / allCards.Count)
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
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private int _latencyMs = -1;  // -1 = not measured
}
