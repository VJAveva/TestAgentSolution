using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestAgentGrpc;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

/// <summary>
/// Monitor view ViewModel for one agent at a time.
/// Lifecycle: LoadAgent(name) starts 2-second telemetry polling; UnloadAgent() stops it.
/// </summary>
public partial class MonitorVM : ObservableObject
{
    private const string Empty = "<empty>";

    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IEventAggregator _events;
    private readonly Dispatcher _uiDispatcher;

    private DispatcherTimer? _telemetryTimer;
    private DispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _pollingCts;
    private IDisposable? _agentOutputSub;
    private IDisposable? _locksChangedSub;
    private IDisposable? _nodeProgressSub;

    /// <summary>Timestamp of the last real-time NodeProgress update. Used to prevent
    /// timer-based refreshes from overwriting live action data.</summary>
    private DateTime _lastNodeProgressUtc;

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
        _events = events;
        _uiDispatcher = uiDispatcher;

        BackCommand = new RelayCommand(() => BackRequested?.Invoke());
        ForceReleaseCommand = new RelayCommand(ForceRelease, () => IsLocked);
        RebootCommand = new AsyncRelayCommand(RebootAsync, () => IsOnline);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync);
        SwitchTabCommand = new RelayCommand<string>(tab => ActiveTab = tab ?? "LiveLog");
        ClearLogCommand = new RelayCommand(() => LiveLog.Clear());
    }

    public event Action? BackRequested;

    // ── Identity (survives offline) ──
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _agentInitials = "";
    [ObservableProperty] private string _agentAddress = Empty;
    [ObservableProperty] private string _agentVersion = Empty;
    [ObservableProperty] private string _uptimeDisplay = Empty;

    // ── Status ──
    [ObservableProperty] private string _statusText = "Loading...";
    [ObservableProperty] private string _statusKind = "Loading";  // Busy | Free | Offline | Loading
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isLocked;

    // ── Current session (left panel) ──
    [ObservableProperty] private string _sessionPipeline = Empty;
    [ObservableProperty] private string _sessionOwner = Empty;
    [ObservableProperty] private string _sessionStarted = Empty;
    [ObservableProperty] private string _sessionElapsed = Empty;
    [ObservableProperty] private string _sessionStep = Empty;

    // ── Current action (left panel) ──
    [ObservableProperty] private string _actionCommand = Empty;
    [ObservableProperty] private string _actionProgress = Empty;

    // ── System info (left panel) ──
    [ObservableProperty] private string _systemOs = Empty;
    [ObservableProperty] private string _systemCores = Empty;
    [ObservableProperty] private string _systemRamTotal = Empty;
    [ObservableProperty] private string _systemDiskFree = Empty;

    // ── Telemetry: CPU ──
    [ObservableProperty] private string _cpuDisplay = Empty;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuSub = "";
    [ObservableProperty] private string _cpuLevel = "Empty";  // Ok | Warn | Busy | Empty

    // ── Telemetry: Memory ──
    [ObservableProperty] private string _memDisplay = Empty;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _memSub = "";
    [ObservableProperty] private string _memLevel = "Empty";

    // ── Telemetry: Disk ──
    [ObservableProperty] private string _diskDisplay = Empty;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskSub = "";
    [ObservableProperty] private string _diskLevel = "Empty";

    // ── Telemetry: Network/Processes ──
    [ObservableProperty] private string _netDisplay = Empty;
    [ObservableProperty] private double _netHealthPercent;
    [ObservableProperty] private string _netSub = "";
    [ObservableProperty] private string _netLevel = "Empty";

    // ── Tabs ──
    [ObservableProperty] private string _activeTab = "LiveLog";
    public ObservableCollection<LiveLogEntryVM> LiveLog { get; } = new();
    public ObservableCollection<DiagnosticVM> Diagnostics { get; } = new();
    public ObservableCollection<HistoryEntryVM> History { get; } = new();

    // ── Commands ──
    public ICommand BackCommand { get; }
    public IRelayCommand ForceReleaseCommand { get; }
    public IAsyncRelayCommand RebootCommand { get; }
    public IAsyncRelayCommand RunDiagnosticsCommand { get; }
    public ICommand SwitchTabCommand { get; }
    public ICommand ClearLogCommand { get; }

    // ═══════════════════════════════════════════════════
    // PUBLIC: Load / Unload
    // ═══════════════════════════════════════════════════

    public void LoadAgent(string agentName)
    {
        UnloadAgent();

        AgentName = agentName;
        AgentInitials = ComputeInitials(agentName);
        AgentAddress = _dispatcher.GetAgentAddress(agentName) ?? Empty;

        RefreshSessionInfo();
        RefreshHistory();

        _agentOutputSub = _events.Subscribe<AgentOutputEvent>(OnAgentOutput);
        _locksChangedSub = _events.Subscribe<AgentLocksChangedEvent>(_ =>
            _uiDispatcher.InvokeAsync(RefreshSessionInfo));
        _nodeProgressSub = _events.Subscribe<NodeProgressEvent>(OnNodeProgress);

        StartTelemetryPolling();
        StartElapsedTimer();
    }

    public void UnloadAgent()
    {
        StopTelemetryPolling();
        StopElapsedTimer();
        _agentOutputSub?.Dispose();
        _agentOutputSub = null;
        _locksChangedSub?.Dispose();
        _locksChangedSub = null;
        _nodeProgressSub?.Dispose();
        _nodeProgressSub = null;

        LiveLog.Clear();
        Diagnostics.Clear();
        History.Clear();
        ResetAllFields();
    }

    // ═══════════════════════════════════════════════════
    // SESSION INFO
    // ═══════════════════════════════════════════════════

    private void RefreshSessionInfo()
    {
        if (string.IsNullOrEmpty(AgentName)) return;

        var agentLock = _lockManager.GetLock(AgentName);
        IsLocked = agentLock != null;
        ForceReleaseCommand.NotifyCanExecuteChanged();

        // If NodeProgress set the action fields recently (< 5s), don't overwrite them
        var recentProgress = (DateTime.UtcNow - _lastNodeProgressUtc).TotalSeconds < 5;

        if (agentLock == null)
        {
            SessionPipeline = Empty;
            SessionOwner = Empty;
            SessionStarted = Empty;
            SessionElapsed = Empty;
            SessionStep = Empty;
            if (!recentProgress)
            {
                ActionCommand = Empty;
                ActionProgress = Empty;
            }
            return;
        }

        SessionPipeline = agentLock.WatchItemTag;
        SessionOwner = $"{agentLock.UserId} ({agentLock.Source})";
        SessionStarted = agentLock.LockedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        SessionElapsed = (DateTime.UtcNow - agentLock.LockedAtUtc).ToString(@"hh\:mm\:ss");

        var session = _sessionManager.GetSession(agentLock.SessionId);
        if (session != null)
        {
            var summary = session.GetAgentSummaries()
                .FirstOrDefault(s => string.Equals(s.AgentName, AgentName, StringComparison.OrdinalIgnoreCase));
            if (summary != null)
            {
                SessionStep = $"{summary.CompletedCount} of {summary.TotalCount}";
                // Only populate ActionCommand from session if no real-time progress is active
                if (!recentProgress && ActionProgress == Empty)
                {
                    var running = summary.Actions
                        .FirstOrDefault(a => a.Outcome == ActionOutcome.Unknown);
                    ActionCommand = running != null ? TruncateCommand(running.Command) : Empty;
                }
            }
            else
            {
                SessionStep = Empty;
            }
        }
    }

    // ═══════════════════════════════════════════════════
    // TELEMETRY POLLING (2-second interval)
    // ═══════════════════════════════════════════════════

    private void StartTelemetryPolling()
    {
        _pollingCts = new CancellationTokenSource();
        _telemetryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            async (_, _) => await PollTelemetryAsync(),
            _uiDispatcher);
        _telemetryTimer.Start();
        _ = PollTelemetryAsync();
    }

    private void StopTelemetryPolling()
    {
        _telemetryTimer?.Stop();
        _telemetryTimer = null;
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    private async Task PollTelemetryAsync()
    {
        if (string.IsNullOrEmpty(AgentName) || _pollingCts == null) return;

        try
        {
            var (snapshot, _) = await _dispatcher.TestConnectionAsync(AgentName, _pollingCts.Token);
            if (snapshot == null)
            {
                ApplyOfflineState();
                return;
            }
            ApplyTelemetry(snapshot);
        }
        catch (OperationCanceledException) { }
        catch { ApplyOfflineState(); }
    }

    private void ApplyTelemetry(AgentSnapshot snapshot)
    {
        IsOnline = true;
        RebootCommand.NotifyCanExecuteChanged();

        // Uptime from agent_started
        if (snapshot.AgentStarted != null)
        {
            var uptime = DateTime.UtcNow - snapshot.AgentStarted.ToDateTime();
            UptimeDisplay = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h"
                : $"{(int)uptime.TotalMinutes}m";
        }

        // Current command from snapshot — only update if NodeProgress hasn't set it recently
        var recentProgress = (DateTime.UtcNow - _lastNodeProgressUtc).TotalSeconds < 3;
        if (!recentProgress)
        {
            if (!string.IsNullOrEmpty(snapshot.CurrentCommand))
                ActionCommand = TruncateCommand(snapshot.CurrentCommand);
            else if (snapshot.State != AgentState.Running)
                ActionCommand = Empty; // Agent finished — clear stale command
        }

        // System info from ResourceMetrics
        var m = snapshot.Metrics;
        if (m != null)
        {
            // CPU
            CpuPercent = Math.Round(m.CpuUsagePct, 0);
            CpuDisplay = $"{CpuPercent:F0}%";
            CpuSub = $"{m.ActiveProcessCount} processes";
            CpuLevel = CpuPercent >= 85 ? "Warn" : CpuPercent >= 50 ? "Busy" : "Ok";

            // Memory
            var memUsedGB = m.MemoryUsedMb / 1024.0;
            var memTotalGB = m.MemoryTotalMb / 1024.0;
            MemPercent = memTotalGB > 0 ? Math.Round(memUsedGB / memTotalGB * 100, 0) : 0;
            MemDisplay = $"{m.MemoryUsedMb:F0} / {m.MemoryTotalMb:F0} MB";
            MemSub = memTotalGB > 0 ? $"{100 - MemPercent:F0}% free" : "";
            MemLevel = MemPercent >= 85 ? "Warn" : MemPercent >= 60 ? "Busy" : "Ok";
            SystemRamTotal = m.MemoryTotalMb > 0 ? $"{memTotalGB:F1} GB" : Empty;

            // Disk
            SystemDiskFree = m.DiskFreeGb > 0 ? $"{m.DiskFreeGb:F1} GB free" : Empty;
            DiskDisplay = m.DiskFreeGb > 0 ? $"{m.DiskFreeGb:F1} GB free" : Empty;
            // We don't have disk total, so show free space
            DiskPercent = 0; // Unknown total — treat as ok
            DiskSub = "available space";
            DiskLevel = m.DiskFreeGb < 5 ? "Warn" : m.DiskFreeGb < 20 ? "Busy" : "Ok";

            // Processes as "network" card proxy
            NetDisplay = $"{m.ActiveProcessCount} procs";
            NetHealthPercent = 100;
            NetSub = "active processes";
            NetLevel = m.ActiveProcessCount > 200 ? "Warn" : "Ok";

            SystemOs = !string.IsNullOrEmpty(m.OsDescription) ? m.OsDescription : Empty;
            SystemCores = Empty; // Not available from metrics
        }
        else
        {
            ApplyEmptyMetrics();
        }

        // Status badge
        if (IsLocked)
        {
            StatusKind = "Busy";
            StatusText = $"BUSY · {SessionPipeline}";
        }
        else
        {
            StatusKind = "Free";
            StatusText = "FREE";
        }
    }

    private void ApplyOfflineState()
    {
        IsOnline = false;
        RebootCommand.NotifyCanExecuteChanged();
        StatusKind = "Offline";
        StatusText = "OFFLINE · last poll failed";
        UptimeDisplay = Empty;
        ApplyEmptyMetrics();
    }

    private void ApplyEmptyMetrics()
    {
        CpuDisplay = Empty; CpuPercent = 0; CpuSub = "no data"; CpuLevel = "Empty";
        MemDisplay = Empty; MemPercent = 0; MemSub = "no data"; MemLevel = "Empty";
        DiskDisplay = Empty; DiskPercent = 0; DiskSub = "no data"; DiskLevel = "Empty";
        NetDisplay = Empty; NetHealthPercent = 0; NetSub = "no data"; NetLevel = "Empty";
    }

    private void ResetAllFields()
    {
        AgentName = ""; AgentInitials = ""; AgentAddress = Empty;
        AgentVersion = Empty; UptimeDisplay = Empty;
        StatusText = ""; StatusKind = "Loading"; IsOnline = false; IsLocked = false;
        SessionPipeline = Empty; SessionOwner = Empty;
        SessionStarted = Empty; SessionElapsed = Empty; SessionStep = Empty;
        ActionCommand = Empty; ActionProgress = Empty;
        SystemOs = Empty; SystemCores = Empty; SystemRamTotal = Empty; SystemDiskFree = Empty;
        ApplyEmptyMetrics();
    }

    // ═══════════════════════════════════════════════════
    // ELAPSED TIMER (1-second for session elapsed)
    // ═══════════════════════════════════════════════════

    private void StartElapsedTimer()
    {
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => RefreshSessionInfo(),
            _uiDispatcher);
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
    }

    // ═══════════════════════════════════════════════════
    // LIVE LOG
    // ═══════════════════════════════════════════════════

    private void OnAgentOutput(AgentOutputEvent e)
    {
        if (!string.Equals(e.AgentName, AgentName, StringComparison.OrdinalIgnoreCase))
            return;

        _uiDispatcher.Invoke(() =>
        {
            LiveLog.Add(new LiveLogEntryVM
            {
                Timestamp = e.Timestamp.ToString("HH:mm:ss"),
                Tag = e.Kind == "stderr" ? "ERR" : "INFO",
                Message = e.Line,
            });
            if (LiveLog.Count > 200)
                LiveLog.RemoveAt(0);
        });
    }

    // ═══════════════════════════════════════════════════
    // NODE PROGRESS (real-time action updates)
    // ═══════════════════════════════════════════════════

    private void OnNodeProgress(NodeProgressEvent e)
    {
        if (!string.Equals(e.AgentName, AgentName, StringComparison.OrdinalIgnoreCase))
            return;

        _uiDispatcher.Invoke(() =>
        {
            _lastNodeProgressUtc = DateTime.UtcNow;

            if (e.Status == "Running")
            {
                ActionCommand = !string.IsNullOrEmpty(e.Command) ? TruncateCommand(e.Command) : Empty;
                ActionProgress = e.ProgressPercent.HasValue ? $"{e.ProgressPercent}%" : "Running";
            }
            else if (e.Status is "Success" or "Failed" or "Skipped")
            {
                ActionCommand = Empty;
                ActionProgress = Empty;
                RefreshSessionInfo();
            }
        });
    }

    // ═══════════════════════════════════════════════════
    // HISTORY
    // ═══════════════════════════════════════════════════

    private void RefreshHistory()
    {
        History.Clear();
        var pastSessions = _sessionManager.GetHistory(50)
            .Where(s => s.LockedAgents.Any(a =>
                string.Equals(a, AgentName, StringComparison.OrdinalIgnoreCase)))
            .Take(20);

        foreach (var s in pastSessions)
        {
            History.Add(new HistoryEntryVM
            {
                SessionId = s.SessionId,
                PipelineName = s.WatchItemTag,
                Date = s.StartedUtc.ToLocalTime().ToString("MMM dd HH:mm"),
                Outcome = s.State.ToString(),
                Duration = ((s.CompletedUtc ?? DateTime.UtcNow) - s.StartedUtc).ToString(@"hh\:mm\:ss"),
                UserId = s.UserId,
            });
        }
    }

    // ═══════════════════════════════════════════════════
    // ACTIONS
    // ═══════════════════════════════════════════════════

    private void ForceRelease()
    {
        var result = MessageBox.Show(
            $"Force release lock on '{AgentName}'?\n\nThis removes the lock but does NOT cancel the pipeline.",
            "Force Release", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        _lockManager.ForceRelease(AgentName);
        RefreshSessionInfo();
    }

    private async Task RebootAsync()
    {
        var result = MessageBox.Show(
            $"Reboot agent '{AgentName}'?\n\nAny running test will be interrupted.",
            "Reboot Agent", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var action = new ActionConfig
            {
                Tag = "reboot",
                Type = ActionType.RunRemoteCommand,
                Command = "shutdown",
                Parameters = "/r /t 5",
                AgentName = AgentName,
                IsReboot = true,
            };
            var ctx = new PipelineExecutionContext { SessionId = "reboot-" + AgentName };
            await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Reboot failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        ActiveTab = "Diagnostics";
        Diagnostics.Clear();

        var steps = await _dispatcher.DiagnoseAgentAsync(AgentName);
        foreach (var step in steps)
        {
            Diagnostics.Add(new DiagnosticVM
            {
                Name = step.Name,
                Status = step.Passed ? "ok" : "err",
                Result = step.Detail,
            });
        }
    }

    private static string TruncateCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return Empty;
        var lastSlash = cmd.LastIndexOfAny(['\\', '/']);
        if (lastSlash >= 0 && lastSlash < cmd.Length - 1)
            cmd = cmd[(lastSlash + 1)..];
        var firstSpace = cmd.IndexOf(' ');
        if (firstSpace > 0) cmd = cmd[..firstSpace];
        return cmd.Length > 22 ? cmd[..20] + ".." : cmd;
    }

    private static string ComputeInitials(string name)
    {
        if (string.IsNullOrEmpty(name)) return "??";
        var lastDigit = name.LastIndexOfAny("0123456789".ToCharArray());
        if (lastDigit >= 0)
        {
            var prefix = name[..lastDigit];
            if (prefix.Length >= 1)
                return $"{char.ToUpper(prefix[^1])}{name[lastDigit]}";
        }
        return name.Length >= 2 ? name[..2].ToUpper() : name.ToUpper();
    }
}

// ── Supporting ViewModels ──

public partial class LiveLogEntryVM : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _tag = "INFO";
    [ObservableProperty] private string _message = "";
}

public partial class DiagnosticVM : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "ok";  // ok | warn | err
    [ObservableProperty] private string _result = "";
}

public partial class HistoryEntryVM : ObservableObject
{
    [ObservableProperty] private string _sessionId = "";
    [ObservableProperty] private string _pipelineName = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private string _userId = "";
}
