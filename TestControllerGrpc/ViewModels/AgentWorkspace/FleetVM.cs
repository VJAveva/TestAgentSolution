using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class FleetVM : ObservableObject
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly Dispatcher _uiDispatcher;

    public ObservableCollection<FleetCardVM> Cards { get; } = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _busyCount;
    [ObservableProperty] private int _freeCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private string _filterText = "";

    public event Action<string>? AgentSelected;
    public event Action? RegisterAgentClicked;

    private readonly DispatcherTimer _healthTimer;

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

    /// <summary>
    /// Probes all registered agents every 5 seconds.
    /// Updates health state so Fleet cards reflect actual status after power cycles.
    /// </summary>
    private async Task ProbeHealthAsync()
    {
        var agents = _dispatcher.RegisteredAgents.ToList();
        bool changed = false;

        foreach (var name in agents)
        {
            var health = _dispatcher.GetAgentHealth(name);
            if (health == null) continue;

            var wasHealthy = health.IsHealthy;
            await _dispatcher.TestConnectionAsync(name);
            // TestConnectionAsync calls RecordSuccess/RecordFailure which updates health.IsHealthy
            if (wasHealthy != health.IsHealthy)
                changed = true;
        }

        if (changed)
            Refresh();
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    public void Refresh()
    {
        var agents = _dispatcher.RegisteredAgents.ToList();
        var allHealth = _dispatcher.GetAllAgentHealth();
        var allLocks = _lockManager.GetAllLocks();

        Cards.Clear();
        int busy = 0, free = 0, offline = 0, failed = 0;

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
                var session = _sessionManager.GetSession(agentLock.SessionId);
                var agentSummary = session?.GetAgentSummaries()
                    .FirstOrDefault(s => string.Equals(s.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

                if (agentSummary != null && agentSummary.Status == "Failed")
                {
                    card.Status = "Failed";
                    card.StatusDetail = $"{agentSummary.CompletedCount}/{agentSummary.TotalCount} — has failures";
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
                offline++;
            }
            else
            {
                card.Status = "Free";
                card.StatusDetail = "Idle";
                free++;
            }

            Cards.Add(card);
        }

        TotalCount = Cards.Count;
        BusyCount = busy;
        FreeCount = free;
        OfflineCount = offline;
        FailedCount = failed;
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

public partial class FleetCardVM : ObservableObject
{
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _status = "Free";
    [ObservableProperty] private string _statusDetail = "Idle";
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private int _latencyMs = -1;  // -1 = not measured
}
