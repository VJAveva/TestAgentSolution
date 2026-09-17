using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TestAgentDisplay.Services;
using TestAgentGrpc;

namespace TestAgentDisplay.ViewModels;

/// <summary>What the agent is doing, independent of how it is coloured.</summary>
public enum AgentDisplayState
{
    Offline,
    Ready,
    Running,
    Inactive,
}

/// <summary>Meaning of an output line, so the view decides the colour.</summary>
public enum OutputLineKind
{
    Stdout,
    Muted,
    Command,
    Warn,
    Success,
    Error,
    Separator,
}

/// <summary>
/// ViewModel for a single agent node — live state, output, metrics, history.
/// Tracks snapshot age to indicate data freshness (DISPLAY-002).
/// </summary>
public sealed partial class AgentNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _stateText = "Offline";
    [ObservableProperty] private AgentDisplayState _stateKind = AgentDisplayState.Offline;
    [ObservableProperty] private string _activity = "—";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _currentCommand = "";
    [ObservableProperty] private string _currentExecutionId = "";
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _memoryUsedMb;
    [ObservableProperty] private double _diskFreeGb;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private string _lastEventTime = "—";
    [ObservableProperty] private Brush _stateBrush = Brushes.Gray;
    [ObservableProperty] private string _snapshotAge = "";
    [ObservableProperty] private bool _isSnapshotStale;

    // Windows Update posture (EVENT_WINDOWS_UPDATE, agent-reported level state).
    [ObservableProperty] private bool _hasUpdatePosture;
    [ObservableProperty] private bool _rebootRequired;
    [ObservableProperty] private int _pendingUpdateCount;
    [ObservableProperty] private string _updateSummary = "";
    [ObservableProperty] private string _updateCheckedAt = "";

    public ObservableCollection<OutputLine> OutputLines { get; } = new();
    public ObservableCollection<ExecutionHistoryItem> History { get; } = new();

    private const int MaxOutputLines = 5000;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;

    public void HandleEvent(ExecutionEvent evt)
    {
        LastEventTime = evt.Timestamp?.ToDateTime().ToLocalTime().ToString("HH:mm:ss") ?? "—";
        UpdateSnapshotFreshness();

        switch (evt.EventType)
        {
            case ExecutionEventType.EventQueued:
                CurrentExecutionId = evt.ExecutionId;
                Activity = $"Queued: {evt.Detail}";
                AddOutput($"⏳ [{evt.ExecutionId}] {evt.Detail}", OutputLineKind.Muted);
                break;

            case ExecutionEventType.EventStarted:
                CurrentCommand = $"{evt.Command} {evt.Arguments}";
                Activity = $"Executing: {evt.Command}";
                SetState("Running", AgentDisplayState.Running);
                AddOutput($"🚀 [{evt.ExecutionId}] {evt.Command} {evt.Arguments}", OutputLineKind.Command);
                break;

            case ExecutionEventType.EventStdoutLine:
                AddOutput($"   {evt.OutputLine}", OutputLineKind.Stdout);
                break;

            case ExecutionEventType.EventStderrLine:
                AddOutput($"⚠  {evt.OutputLine}", OutputLineKind.Warn);
                break;

            case ExecutionEventType.EventCompleted:
                var exitKind = evt.ExitCode == 0 ? OutputLineKind.Success : OutputLineKind.Error;
                AddOutput($"✅ [{evt.ExecutionId}] Exit code {evt.ExitCode}", exitKind);
                AddOutput("", OutputLineKind.Separator);
                Activity = $"Completed (exit {evt.ExitCode})";
                CompletedCount++;
                // DISPLAY-002: Clear stale command on completion
                CurrentCommand = "";
                CurrentExecutionId = "";
                SetState("Ready", AgentDisplayState.Ready);
                break;

            case ExecutionEventType.EventFailed:
                AddOutput($"❌ [{evt.ExecutionId}] {evt.ErrorMessage}", OutputLineKind.Error);
                AddOutput("", OutputLineKind.Separator);
                Activity = $"Failed: {evt.ErrorMessage}";
                FailedCount++;
                // DISPLAY-002: Clear stale command on failure
                CurrentCommand = "";
                CurrentExecutionId = "";
                SetState("Ready", AgentDisplayState.Ready);
                break;

            case ExecutionEventType.EventTerminated:
                AddOutput($"🛑 [{evt.ExecutionId}] {evt.Detail}", OutputLineKind.Error);
                Activity = "Terminated";
                FailedCount++;
                // DISPLAY-002: Clear stale command on termination
                CurrentCommand = "";
                CurrentExecutionId = "";
                SetState("Ready", AgentDisplayState.Ready);
                break;

            case ExecutionEventType.EventStateChanged:
                UpdateAgentState(evt.AgentState);
                break;

            case ExecutionEventType.EventHeartbeat:
                if (evt.Metrics is not null)
                {
                    CpuUsage = evt.Metrics.CpuUsagePct;
                    MemoryUsedMb = evt.Metrics.MemoryUsedMb;
                    DiskFreeGb = evt.Metrics.DiskFreeGb;
                }
                // Only apply heartbeat state when not tracking a local execution.
                // Heartbeats during the RunCommand→ExecuteAsync race window could
                // report Ready before the executor has transitioned to Running,
                // causing the "Current Action" display to flicker.
                if (string.IsNullOrEmpty(CurrentExecutionId))
                    UpdateAgentState(evt.AgentState);
                break;

            case ExecutionEventType.EventProgress:
                Activity = $"Progress: {evt.ProgressPct:F0}% — {evt.Detail}";
                break;
            case ExecutionEventType.EventWindowsUpdate:
                ApplyUpdatePosture(evt);
                break;
        }
    }

    /// <summary>
    /// EVENT_WINDOWS_UPDATE carries the agent's complete update posture as JSON in Detail.
    /// A payload we cannot parse is surfaced as a warning line rather than dropped silently.
    /// </summary>
    private void ApplyUpdatePosture(ExecutionEvent evt)
    {
        if (!WindowsUpdateSnapshot.TryParse(evt.Detail, out var posture))
        {
            AddOutput("\u26a0  Windows Update posture received but could not be parsed.", OutputLineKind.Warn);
            return;
        }

        HasUpdatePosture = true;
        RebootRequired = posture.RebootRequired;
        PendingUpdateCount = posture.PendingCount;
        UpdateSummary = posture.Summarize();

        var detected = posture.DetectedUtc == default
            ? evt.Timestamp?.ToDateTime() ?? DateTime.UtcNow
            : posture.DetectedUtc.UtcDateTime;
        UpdateCheckedAt = detected.ToLocalTime().ToString("HH:mm:ss dd-MMM");

        var kind = posture.RebootRequired ? OutputLineKind.Warn : OutputLineKind.Muted;
        AddOutput($"\U0001f6e1  Windows Update: {UpdateSummary}", kind);

        foreach (var item in posture.Items.Where(i => !string.IsNullOrWhiteSpace(i.KbId)).Take(10))
        {
            var failed = item.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase);
            var suffix = failed && !string.IsNullOrWhiteSpace(item.ResultCode) ? $" ({item.ResultCode})" : "";
            AddOutput($"     {item.KbId} {item.Result}{suffix} {item.Title}".TrimEnd(),
                failed ? OutputLineKind.Error : OutputLineKind.Muted);        }
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            SetState("Offline", AgentDisplayState.Offline);
            Activity = "Disconnected";
            // DISPLAY-002: Clear command on disconnect to avoid stale display
            CurrentCommand = "";
        }
    }

    public void ApplySnapshot(AgentSnapshot snap)
    {
        // Use the agent's configured name from the snapshot if available,
        // instead of the hostname extracted from the URL
        if (!string.IsNullOrWhiteSpace(snap.AgentName))
            DisplayName = snap.AgentName;

        // Only update state/activity from snapshot if we're not tracking an
        // active execution locally. The event stream is the authoritative source
        // during execution — snapshot polling would overwrite with stale data
        // and cause the "Current Action" display to flicker.
        if (string.IsNullOrEmpty(CurrentExecutionId))
        {
            UpdateAgentState(snap.State);
            Activity = snap.CurrentActivity;
            CurrentCommand = snap.CurrentCommand;
            CurrentExecutionId = snap.CurrentExecutionId;
        }

        CompletedCount = snap.ExecutionsCompleted;
        FailedCount = snap.ExecutionsFailed;
        if (snap.Metrics is not null)
        {
            CpuUsage = snap.Metrics.CpuUsagePct;
            MemoryUsedMb = snap.Metrics.MemoryUsedMb;
            DiskFreeGb = snap.Metrics.DiskFreeGb;
        }

        // DISPLAY-002: Track snapshot freshness
        _lastSnapshotUtc = DateTime.UtcNow;
        UpdateSnapshotFreshness();
    }

    /// <summary>
    /// Updates the snapshot age display. Data older than 60s is considered stale.
    /// Called from ApplySnapshot (which sets _lastSnapshotUtc) and periodically.
    /// </summary>
    public void UpdateSnapshotFreshness()
    {
        var age = DateTime.UtcNow - _lastSnapshotUtc;
        IsSnapshotStale = age > TimeSpan.FromSeconds(60);
        SnapshotAge = age.TotalSeconds < 5 ? "just now"
            : age.TotalSeconds < 60 ? $"{age.TotalSeconds:F0}s ago"
            : age.TotalMinutes < 60 ? $"{age.TotalMinutes:F0}m ago"
            : $"{age.TotalHours:F0}h ago";
    }

    public void ApplyHistory(ExecutionHistoryReply history)
    {
        History.Clear();
        foreach (var r in history.Records)
        {
            var commandText = $"{r.Command} {r.Arguments}".Trim();
            var operation = InferOperation(r.Command, r.Arguments);

            History.Add(new ExecutionHistoryItem
            {
                ExecutionId = r.ExecutionId,
                Command = r.Command,
                CommandText = commandText,
                Operation = operation,
                Outcome = r.Outcome switch
                {
                    ExecutionOutcome.OutcomeSuccess    => "✓ Success",
                    ExecutionOutcome.OutcomeFailed     => "✗ Failed",
                    ExecutionOutcome.OutcomeTerminated => "⊘ Killed",
                    ExecutionOutcome.OutcomeTimedOut   => "⏱ Timeout",
                    _ => "?"
                },
                ExitCode = r.ExitCode,
                Duration = (r.Finished is not null && r.Started is not null)
                    ? (r.Finished.ToDateTime() - r.Started.ToDateTime()).ToString(@"hh\:mm\:ss")
                    : "—",
                StartedAt = r.Started?.ToDateTime().ToLocalTime().ToString("HH:mm:ss dd-MMM") ?? "—",
                StdoutLineCount = r.StdoutLines.Count,
                StderrLineCount = r.StderrLines.Count,
            });
        }
    }

    /// <summary>Infers a human-readable operation description from the command and arguments.</summary>
    private static string InferOperation(string command, string arguments)
    {
        var cmd = command.Trim().Trim('"').ToLowerInvariant();
        var args = arguments.ToLowerInvariant();
        var ext = "";
        // GetExtension only throws on invalid path characters; a malformed command must not
        // stop us classifying the rest of the history row.
        try { ext = System.IO.Path.GetExtension(cmd); }
        catch (ArgumentException) { ext = ""; }

        if (cmd.Contains("build") || args.Contains("build")) return "Build";
        if (cmd.Contains("deploy") || args.Contains("deploy")) return "Deploy";
        if (cmd.Contains("test") || args.Contains("test") || cmd.Contains("smoke")) return "Test";
        if (cmd.Contains("install") || args.Contains("install") || ext == ".msi") return "Install";
        if (cmd.Contains("setup") || args.Contains("setup")) return "Setup";
        if (cmd == "shutdown" || cmd == "restart" || args.Contains("/r")) return "Reboot";
        if (cmd.Contains("copy") || args.Contains("robocopy")) return "FileCopy";
        if (ext == ".ps1") return "PowerShell";
        if (ext is ".bat" or ".cmd") return "Script";
        if (cmd is "cmd" or "cmd.exe") return "Command";
        if (cmd is "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe") return "PowerShell";
        return "Execute";
    }

    private void UpdateAgentState(AgentState state)
    {
        switch (state)
        {
            case AgentState.Ready:    SetState("Ready", AgentDisplayState.Ready); break;
            case AgentState.Running:  SetState("Running", AgentDisplayState.Running); break;
            case AgentState.Inactive: SetState("Inactive", AgentDisplayState.Inactive); break;
        }
    }

    private void SetState(string text, AgentDisplayState kind)
    {
        StateText = text;
        StateKind = kind;

        // Reboot-required outranks the run state: an agent that is Ready but needs a reboot
        // must not look identical to a healthy one.
        var key = kind switch
        {
            AgentDisplayState.Ready    => RebootRequired ? ThemeBrushes.AgentRebootRequired : ThemeBrushes.AgentReady,
            AgentDisplayState.Running  => ThemeBrushes.AgentRunning,
            AgentDisplayState.Inactive => ThemeBrushes.AgentInactive,
            _                          => ThemeBrushes.AgentOffline,
        };
        StateBrush = ThemeBrushes.Resolve(key, Brushes.Gray);
    }

    private void AddOutput(string text, OutputLineKind kind)
    {
        OutputLines.Add(new OutputLine(text, kind));
        while (OutputLines.Count > MaxOutputLines)
            OutputLines.RemoveAt(0);
    }
}

public sealed class OutputLine
{
    public string Text { get; }
    public OutputLineKind Kind { get; }
    public Brush Foreground { get; }

    public OutputLine(string text, OutputLineKind kind)
    {
        Text = text;
        Kind = kind;
        Foreground = ThemeBrushes.Resolve(KeyFor(kind), Brushes.Gainsboro);
    }

    private static string KeyFor(OutputLineKind kind) => kind switch
    {
        OutputLineKind.Muted     => ThemeBrushes.OutputMuted,
        OutputLineKind.Command   => ThemeBrushes.OutputCommand,
        OutputLineKind.Warn      => ThemeBrushes.OutputWarn,
        OutputLineKind.Success   => ThemeBrushes.OutputSuccess,
        OutputLineKind.Error     => ThemeBrushes.OutputError,
        OutputLineKind.Separator => ThemeBrushes.OutputSeparator,
        _                    => ThemeBrushes.OutputStdout,
    };
}

public sealed class ExecutionHistoryItem
{
    public string ExecutionId { get; init; } = "";
    public string Command { get; init; } = "";
    public string CommandText { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int ExitCode { get; init; }
    public string Duration { get; init; } = "";
    public string StartedAt { get; init; } = "";
    public int StdoutLineCount { get; init; }
    public int StderrLineCount { get; init; }
}
