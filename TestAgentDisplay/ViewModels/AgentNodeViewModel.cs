using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TestAgentGrpc;

namespace TestAgentDisplay.ViewModels;

/// <summary>
/// ViewModel for a single agent node — live state, output, metrics, history.
/// </summary>
public sealed partial class AgentNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _stateText = "Offline";
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

    public ObservableCollection<OutputLine> OutputLines { get; } = new();
    public ObservableCollection<ExecutionHistoryItem> History { get; } = new();

    private const int MaxOutputLines = 5000;

    public void HandleEvent(ExecutionEvent evt)
    {
        LastEventTime = evt.Timestamp?.ToDateTime().ToLocalTime().ToString("HH:mm:ss") ?? "—";

        switch (evt.EventType)
        {
            case ExecutionEventType.EventQueued:
                CurrentExecutionId = evt.ExecutionId;
                Activity = $"Queued: {evt.Detail}";
                AddOutput($"⏳ [{evt.ExecutionId}] {evt.Detail}", "#90A4AE");
                break;

            case ExecutionEventType.EventStarted:
                CurrentCommand = $"{evt.Command} {evt.Arguments}";
                Activity = $"Executing: {evt.Command}";
                SetState("Running", "#00C9A7");
                AddOutput($"🚀 [{evt.ExecutionId}] {evt.Command} {evt.Arguments}", "#64B5F6");
                break;

            case ExecutionEventType.EventStdoutLine:
                AddOutput($"   {evt.OutputLine}", "#E0E0E0");
                break;

            case ExecutionEventType.EventStderrLine:
                AddOutput($"⚠  {evt.OutputLine}", "#FFA726");
                break;

            case ExecutionEventType.EventCompleted:
                var exitColor = evt.ExitCode == 0 ? "#66BB6A" : "#EF5350";
                AddOutput($"✅ [{evt.ExecutionId}] Exit code {evt.ExitCode}", exitColor);
                AddOutput("", "#444444");
                Activity = $"Completed (exit {evt.ExitCode})";
                CompletedCount++;
                CurrentCommand = "";
                SetState("Ready", "#00C9A7");
                break;

            case ExecutionEventType.EventFailed:
                AddOutput($"❌ [{evt.ExecutionId}] {evt.ErrorMessage}", "#EF5350");
                AddOutput("", "#444444");
                Activity = $"Failed: {evt.ErrorMessage}";
                FailedCount++;
                CurrentCommand = "";
                SetState("Ready", "#00C9A7");
                break;

            case ExecutionEventType.EventTerminated:
                AddOutput($"🛑 [{evt.ExecutionId}] {evt.Detail}", "#EF5350");
                Activity = "Terminated";
                FailedCount++;
                CurrentCommand = "";
                SetState("Ready", "#00C9A7");
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
                UpdateAgentState(evt.AgentState);
                break;

            case ExecutionEventType.EventProgress:
                Activity = $"Progress: {evt.ProgressPct:F0}% — {evt.Detail}";
                break;
        }
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            SetState("Offline", "#78909C");
            Activity = "Disconnected";
        }
    }

    public void ApplySnapshot(AgentSnapshot snap)
    {
        UpdateAgentState(snap.State);
        Activity = snap.CurrentActivity;
        CurrentCommand = snap.CurrentCommand;
        CurrentExecutionId = snap.CurrentExecutionId;
        CompletedCount = snap.ExecutionsCompleted;
        FailedCount = snap.ExecutionsFailed;
        if (snap.Metrics is not null)
        {
            CpuUsage = snap.Metrics.CpuUsagePct;
            MemoryUsedMb = snap.Metrics.MemoryUsedMb;
            DiskFreeGb = snap.Metrics.DiskFreeGb;
        }
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
        try { ext = System.IO.Path.GetExtension(cmd); } catch { }

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
            case AgentState.Ready:    SetState("Ready", "#00C9A7"); break;
            case AgentState.Running:  SetState("Running", "#42A5F5"); break;
            case AgentState.Inactive: SetState("Inactive", "#78909C"); break;
        }
    }

    private void SetState(string text, string hex)
    {
        StateText = text;
        StateBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void AddOutput(string text, string colorHex)
    {
        OutputLines.Add(new OutputLine(text, colorHex));
        while (OutputLines.Count > MaxOutputLines)
            OutputLines.RemoveAt(0);
    }
}

public sealed class OutputLine
{
    public string Text { get; }
    public Brush Foreground { get; }

    public OutputLine(string text, string colorHex)
    {
        Text = text;
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        Foreground.Freeze(); // Thread-safe for WPF cross-thread binding
    }
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
