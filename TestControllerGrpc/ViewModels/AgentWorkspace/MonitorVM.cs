using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class MonitorVM : ObservableObject
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly Dispatcher _uiDispatcher;
    private DispatcherTimer? _refreshTimer;

    // Agent identity
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _agentAddress = "";
    [ObservableProperty] private string _status = "—";

    // Lock / session info
    [ObservableProperty] private string _sessionPipeline = "";
    [ObservableProperty] private string _sessionElapsed = "";
    [ObservableProperty] private bool _isBusy;

    // Live log
    public ObservableCollection<LogLineVM> LiveLog { get; } = new();

    public event Action? BackRequested;

    public MonitorVM(
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

        events.Subscribe<AgentOutputEvent>(e =>
        {
            if (!string.IsNullOrEmpty(AgentName) &&
                string.Equals(e.AgentName, AgentName, StringComparison.OrdinalIgnoreCase))
            {
                uiDispatcher.InvokeAsync(() => AddLogLine(e));
            }
        });

        events.Subscribe<AgentLocksChangedEvent>(_ =>
            uiDispatcher.InvokeAsync(RefreshSessionInfo));
    }

    public void LoadAgent(string agentName)
    {
        StopRefresh();

        AgentName = agentName;
        AgentAddress = _dispatcher.GetAgentAddress(agentName) ?? "unknown";
        LiveLog.Clear();

        RefreshSessionInfo();
        StartRefresh();
    }

    private void StartRefresh()
    {
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(3),
            DispatcherPriority.Background,
            (_, _) => RefreshSessionInfo(),
            _uiDispatcher);
        _refreshTimer.Start();
    }

    private void StopRefresh()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void RefreshSessionInfo()
    {
        if (string.IsNullOrEmpty(AgentName)) return;

        var health = _dispatcher.GetAgentHealth(AgentName);
        var agentLock = _lockManager.GetLock(AgentName);

        if (health != null && !health.IsHealthy)
        {
            Status = "Offline";
            IsBusy = false;
        }
        else if (agentLock != null)
        {
            Status = "Busy";
            IsBusy = true;
            SessionPipeline = agentLock.WatchItemTag;
            var elapsed = DateTime.UtcNow - agentLock.LockedAtUtc;
            SessionElapsed = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }
        else
        {
            Status = "Free";
            IsBusy = false;
            SessionPipeline = "";
            SessionElapsed = "";
        }
    }

    private void AddLogLine(AgentOutputEvent e)
    {
        LiveLog.Add(new LogLineVM
        {
            Timestamp = e.Timestamp.ToString("HH:mm:ss"),
            Kind = e.Kind == "stderr" ? "ERR" : "OUT",
            Line = e.Line,
        });

        // Cap at 500 lines
        while (LiveLog.Count > 500)
            LiveLog.RemoveAt(0);
    }

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke();

    [RelayCommand]
    private void ForceRelease()
    {
        if (string.IsNullOrEmpty(AgentName)) return;

        var result = MessageBox.Show(
            $"Force release agent '{AgentName}'?\n\nThis removes the lock without cancelling the running pipeline.",
            "Force Release", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _lockManager.ForceRelease(AgentName);
        RefreshSessionInfo();
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (string.IsNullOrEmpty(AgentName)) return;

        Status = "Testing...";
        var (snapshot, error) = await _dispatcher.TestConnectionAsync(AgentName);
        Status = snapshot != null ? "Online" : $"Failed: {error}";
    }
}

public partial class LogLineVM : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _line = "";
}
