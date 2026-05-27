using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Standalone, read-only observer for execution history. Subscribes to
/// IEventAggregator events and reads completed sessions from
/// ExecutionSessionManager. Does NOT write, dispatch, or interact with
/// gRPC channels in any way.
/// </summary>
public sealed partial class ExecutionHistoryPanelVM : ObservableObject, IDisposable
{
    private readonly ExecutionSessionManager _sessions;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _completedSub;
    private readonly IDisposable _progressSub;

    private const int MaxLiveActions = 500;
    private const int MaxSessions = 50;

    public ExecutionHistoryPanelVM(IEventAggregator events, ExecutionSessionManager sessions)
    {
        _sessions = sessions;
        _dispatcher = Application.Current.Dispatcher;

        // Auto-refresh when a session completes
        _completedSub = events.Subscribe<ExecutionCompletedEvent>(_ =>
            _dispatcher.InvokeAsync(RefreshSessions));

        // Track live action progress (current command / status)
        _progressSub = events.Subscribe<NodeProgressEvent>(OnNodeProgress);

        // Load existing history on construction
        RefreshSessions();
    }

    // ── Sessions ────────────────────────────────────────────────────
    public ObservableCollection<HistorySessionItem> Sessions { get; } = new();

    [ObservableProperty] private HistorySessionItem? _selectedSession;

    // ── Actions for selected session ────────────────────────────────
    public ObservableCollection<HistoryActionItem> SessionActions { get; } = new();

    // ── Live action feed (rolling log of NodeProgressEvent) ─────────
    public ObservableCollection<LiveActionItem> LiveActions { get; } = new();

    // ── Commands ────────────────────────────────────────────────────
    [RelayCommand]
    private void RefreshSessions()
    {
        Sessions.Clear();
        SessionActions.Clear();

        foreach (var s in _sessions.GetHistory(MaxSessions))
        {
            Sessions.Add(new HistorySessionItem
            {
                SessionId = s.SessionId,
                WatchItemTag = s.WatchItemTag,
                EventType = s.EventType,
                StartedUtc = s.StartedUtc,
                CompletedUtc = s.CompletedUtc,
                State = s.State,
                TotalActions = s.TotalActions,
                FailedCount = s.FailedCount,
                SucceededCount = s.SucceededCount,
                UserId = s.UserId,
                Source = s.Source,
            });
        }
    }

    [RelayCommand]
    private void ClearLiveLog() => LiveActions.Clear();

    partial void OnSelectedSessionChanged(HistorySessionItem? value)
    {
        SessionActions.Clear();
        if (value is null) return;

        var session = _sessions.GetSession(value.SessionId);
        if (session is null) return;

        foreach (var r in session.ActionResults.OrderBy(r => r.Sequence))
        {
            SessionActions.Add(new HistoryActionItem
            {
                ActionTag = r.ActionTag,
                ActionType = r.ActionType,
                AgentName = r.AgentName ?? "Controller",
                Command = r.Command,
                Outcome = r.Outcome,
                Duration = r.DurationText,
                ExitCode = r.ExitCode,
                ErrorMessage = r.ErrorMessage,
                StartedUtc = r.StartedUtc,
            });
        }
    }

    // ── Live progress handler ───────────────────────────────────────
    private void OnNodeProgress(NodeProgressEvent e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            LiveActions.Add(new LiveActionItem
            {
                Timestamp = DateTime.Now,
                SessionId = e.SessionId,
                AgentName = e.AgentName,
                NodeTag = e.NodeTag,
                ActionType = e.ActionType,
                Command = e.Command,
                Status = e.Status,
                ExitCode = e.ExitCode,
                ErrorMessage = e.ErrorMessage,
                Duration = e.Duration,
            });

            while (LiveActions.Count > MaxLiveActions)
                LiveActions.RemoveAt(0);
        });
    }

    public void Dispose()
    {
        _completedSub.Dispose();
        _progressSub.Dispose();
    }
}

// ── Item models (not touching any existing models) ──────────────────

public sealed class HistorySessionItem
{
    public string SessionId { get; init; } = "";
    public string WatchItemTag { get; init; } = "";
    public string EventType { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public SessionState State { get; init; }
    public int TotalActions { get; init; }
    public int FailedCount { get; init; }
    public int SucceededCount { get; init; }
    public string UserId { get; init; } = "";
    public string Source { get; init; } = "";

    public string StateIcon => State switch
    {
        SessionState.Completed => "\u2713",
        SessionState.PartialFailure => "\u26A0",
        SessionState.Failed => "\u2717",
        SessionState.Running => "\u25B6",
        _ => ""
    };

    public string DurationDisplay
    {
        get
        {
            if (CompletedUtc is null) return "—";
            var d = CompletedUtc.Value - StartedUtc;
            return d.TotalSeconds < 1 ? $"{d.TotalMilliseconds:F0}ms"
                 : d.TotalMinutes < 1 ? $"{d.TotalSeconds:F1}s"
                 : d.ToString(@"mm\:ss");
        }
    }

    public string TimeDisplay => StartedUtc.ToLocalTime().ToString("HH:mm:ss");
}

public sealed class HistoryActionItem
{
    public string ActionTag { get; init; } = "";
    public string ActionType { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string Command { get; init; } = "";
    public ActionOutcome Outcome { get; init; }
    public string Duration { get; init; } = "";
    public int? ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime StartedUtc { get; init; }

    public string OutcomeIcon => Outcome switch
    {
        ActionOutcome.Success => "\u2713",
        ActionOutcome.Failed => "\u2717",
        ActionOutcome.Terminated => "\u2298",
        ActionOutcome.TimedOut => "\u23F1",
        _ => "\u2026"
    };

    public string TimeDisplay => StartedUtc.ToLocalTime().ToString("HH:mm:ss.fff");
}

public sealed class LiveActionItem
{
    public DateTime Timestamp { get; init; }
    public string SessionId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string NodeTag { get; init; } = "";
    public string ActionType { get; init; } = "";
    public string Command { get; init; } = "";
    public string Status { get; init; } = "";
    public int? ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Duration { get; init; }

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");

    public string StatusIcon => Status switch
    {
        "Running" => "\u25B6",
        "Success" => "\u2713",
        "Failed" => "\u2717",
        "Skipped" => "\u2014",
        _ => "\u2026"
    };
}
