using System.Collections.Concurrent;
using System.Text.Json;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Manages the lifecycle of execution sessions — tracks active and completed
/// pipeline runs, records per-action results, and supports retry of failed actions.
/// Optionally persists session snapshots to disk so the dashboard can recover
/// execution state after a crash (e.g. UIAutomationCore stack overflow).
/// </summary>
public sealed class ExecutionSessionManager
{
    private readonly ConcurrentDictionary<string, ExecutionSession> _active = new();
    private readonly object _historyLock = new();
    private readonly List<ExecutionSession> _history = [];
    private const int MaxHistory = 50;

    private readonly IEventAggregator? _events;
    private readonly string? _persistPath;
    private readonly object _persistLock = new();
    private long _lastPersistTicks;

    /// <summary>
    /// Default ctor for legacy/test code paths that don't need event publishing.
    /// </summary>
    public ExecutionSessionManager() { }

    /// <summary>
    /// DI ctor: when an event aggregator is supplied, the session manager
    /// publishes <see cref="NodeProgressEvent"/> on every action start and
    /// finish so the dashboard can update without polling.
    /// </summary>
    public ExecutionSessionManager(IEventAggregator events) { _events = events; }

    /// <summary>
    /// Full DI ctor with persistence path. Sessions are persisted to the
    /// given file path and restored on construction (crash recovery).
    /// </summary>
    public ExecutionSessionManager(IEventAggregator events, string persistPath)
    {
        _events = events;
        _persistPath = persistPath;
        RestoreFromDisk();
    }

    /// <summary>
    /// Begins a new execution session and tracks it as active.
    /// If a session with the given <paramref name="sessionId"/> already exists
    /// (e.g. pre-created by the controller to set UserId/Source/LockedAgents),
    /// returns the existing session instead of overwriting it.
    /// </summary>
    public ExecutionSession BeginSession(
        string watchItemTag, string eventType,
        Dictionary<string, string> resolvedParameters,
        List<IActionNode> snapshotNodes,
        string? sessionId = null)
    {
        // If a session with this ID was already pre-registered (e.g. by ExecutionController
        // before handing off to the executor), return it to preserve UserId/Source/LockedAgents.
        if (sessionId != null && _active.TryGetValue(sessionId, out var existing))
            return existing;

        var session = new ExecutionSession
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..12],
            WatchItemTag = watchItemTag,
            EventType = eventType,
            ResolvedParameters = new Dictionary<string, string>(resolvedParameters, StringComparer.OrdinalIgnoreCase),
            SnapshotNodes = snapshotNodes
        };
        _active[session.SessionId] = session;
        PersistToDisk();
        return session;
    }

    /// <summary>
    /// Notifies subscribers that an action has started running. Executors should
    /// call this immediately before invoking the action, so the dashboard can
    /// flip the pill from Pending to Running without waiting for completion.
    /// </summary>
    public void BeginAction(string sessionId, ActionExecutionResult result)
    {
        _events?.Publish(new NodeProgressEvent(
            SessionId: sessionId,
            AgentName: result.AgentName ?? "Controller",
            NodeTag: result.ActionTag,
            ActionType: result.ActionType,
            Command: result.Command,
            Status: "Running"));
    }

    /// <summary>Records a per-action result into the active session. Thread-safe for parallel execution.</summary>
    public void RecordResult(string sessionId, ActionExecutionResult result)
    {
        if (_active.TryGetValue(sessionId, out var session))
            session.AddResult(result);

        _events?.Publish(new NodeProgressEvent(
            SessionId: sessionId,
            AgentName: result.AgentName ?? "Controller",
            NodeTag: result.ActionTag,
            ActionType: result.ActionType,
            Command: result.Command,
            Status: MapOutcome(result.Outcome),
            ExitCode: result.ExitCode,
            ErrorMessage: result.ErrorMessage,
            Duration: result.DurationText));

        // Throttled persist: at most once per 5 seconds during execution
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastPersistTicks) > TimeSpan.TicksPerSecond * 5)
        {
            Interlocked.Exchange(ref _lastPersistTicks, now);
            PersistToDisk();
        }
    }

    private static string MapOutcome(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success    => "Success",
        ActionOutcome.Failed     => "Failed",
        ActionOutcome.Terminated => "Failed",
        ActionOutcome.TimedOut   => "Failed",
        _                        => "Running",
    };

    /// <summary>Marks a session complete and archives it into history.</summary>
    public void CompleteSession(string sessionId)
    {
        if (_active.TryRemove(sessionId, out var session))
        {
            session.CompletedUtc = DateTime.UtcNow;
            session.State = session.FailedCount == 0
                ? SessionState.Completed
                : session.SucceededCount == 0
                    ? SessionState.Failed
                    : SessionState.PartialFailure;

            lock (_historyLock)
            {
                _history.Insert(0, session);
                while (_history.Count > MaxHistory)
                    _history.RemoveAt(_history.Count - 1);
            }

            PersistToDisk();
        }
    }

    /// <summary>Gets the most recent completed session for a WatchItem tag.</summary>
    public ExecutionSession? GetLastSession(string watchItemTag)
    {
        lock (_historyLock)
        {
            return _history.FirstOrDefault(s =>
                string.Equals(s.WatchItemTag, watchItemTag, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Gets a session by ID (active or history).</summary>
    public ExecutionSession? GetSession(string sessionId)
    {
        if (_active.TryGetValue(sessionId, out var active)) return active;
        lock (_historyLock) { return _history.FirstOrDefault(s => s.SessionId == sessionId); }
    }

    /// <summary>Returns the N most recent completed sessions.</summary>
    public IReadOnlyList<ExecutionSession> GetHistory(int count)
    {
        lock (_historyLock) { return _history.Take(count).ToList(); }
    }

    /// <summary>Returns the retryable (failed) action nodes from a given session.</summary>
    public List<IActionNode> GetRetryableNodes(string sessionId)
    {
        ExecutionSession? session;
        lock (_historyLock)
        {
            session = _history.FirstOrDefault(s => s.SessionId == sessionId);
        }
        session ??= _active.Values.FirstOrDefault(s => s.SessionId == sessionId);

        return session?.FailedActions
            .Where(a => a.OriginalNode is not null)
            .Select(a => a.OriginalNode!)
            .ToList() ?? [];
    }

    /// <summary>Checks if a WatchItem has an active (in-progress) execution.</summary>
    public bool HasActiveExecution(string watchItemTag)
        => _active.Values.Any(s =>
            string.Equals(s.WatchItemTag, watchItemTag, StringComparison.OrdinalIgnoreCase));

    public int ActiveExecutionCount => _active.Count;
    public bool HasAnyActiveExecution => !_active.IsEmpty;

    /// <summary>Returns a snapshot of all active sessions for API consumers.</summary>
    public List<ExecutionSession> GetActiveSessions()
        => _active.Values.ToList();

    /// <summary>Cancels a single active session by ID, moving it to history.</summary>
    public bool CancelSession(string sessionId)
    {
        if (!_active.TryRemove(sessionId, out var session))
            return false;

        session.RequestCancellation();
        session.CompletedUtc = DateTime.UtcNow;
        session.State = SessionState.Failed;

        lock (_historyLock)
        {
            _history.Insert(0, session);
            while (_history.Count > MaxHistory)
                _history.RemoveAt(_history.Count - 1);
        }

        PersistToDisk();
        return true;
    }

    /// <summary>Cancels all active sessions, moving them to history as cancelled.</summary>
    public List<string> CancelAll()
    {
        var cancelledTags = new List<string>();
        var ids = _active.Keys.ToList();
        foreach (var id in ids)
        {
            if (_active.TryRemove(id, out var session))
            {
                session.RequestCancellation();
                session.CompletedUtc = DateTime.UtcNow;
                session.State = SessionState.Failed;
                cancelledTags.Add(session.WatchItemTag);

                lock (_historyLock)
                {
                    _history.Insert(0, session);
                    while (_history.Count > MaxHistory)
                        _history.RemoveAt(_history.Count - 1);
                }
            }
        }

        if (cancelledTags.Count > 0)
            PersistToDisk();
        return cancelledTags;
    }

    /// <summary>Returns history loaded from disk (for dashboard recovery after crash).</summary>
    public IReadOnlyList<PersistedSession> GetPersistedHistory()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath))
            return Array.Empty<PersistedSession>();

        try
        {
            var json = File.ReadAllText(_persistPath);
            return JsonSerializer.Deserialize<PersistedSession[]>(json) ?? Array.Empty<PersistedSession>();
        }
        catch { return Array.Empty<PersistedSession>(); }
    }

    // ── Persistence ──────────────────────────────────────────────────────

    private void PersistToDisk()
    {
        if (string.IsNullOrEmpty(_persistPath)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_persistLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_persistPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    List<PersistedSession> snapshot;
                    lock (_historyLock)
                    {
                        snapshot = _history.Select(ToPersistedSession).ToList();
                    }

                    // Also snapshot active sessions (for crash recovery)
                    foreach (var active in _active.Values)
                        snapshot.Add(ToPersistedSession(active));

                    var json = JsonSerializer.Serialize(snapshot,
                        new JsonSerializerOptions { WriteIndented = true });

                    var tempPath = _persistPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _persistPath, overwrite: true);
                }
                catch
                {
                    // Persistence failure is non-fatal
                }
            }
        });
    }

    private void RestoreFromDisk()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var entries = JsonSerializer.Deserialize<PersistedSession[]>(json);
            if (entries == null) return;

            lock (_historyLock)
            {
                foreach (var entry in entries)
                {
                    // Sessions that were "Running" when we crashed are now dead
                    var state = entry.State == nameof(SessionState.Running)
                        ? SessionState.Failed
                        : Enum.TryParse<SessionState>(entry.State, out var s) ? s : SessionState.Failed;

                    var session = new ExecutionSession
                    {
                        SessionId = entry.SessionId,
                        WatchItemTag = entry.WatchItemTag,
                        EventType = entry.EventType,
                        UserId = entry.UserId,
                        Source = entry.Source,
                        LockedAgents = entry.LockedAgents ?? Array.Empty<string>(),
                        ResolvedParameters = entry.ResolvedParameters ?? new(),
                        State = state,
                        CompletedUtc = DateTime.TryParse(entry.CompletedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                            ? dt : DateTime.UtcNow,
                    };

                    // Restore action results
                    foreach (var ar in entry.ActionResults ?? [])
                    {
                        var actionResult = new ActionExecutionResult
                        {
                            ActionTag = ar.ActionTag,
                            ActionType = ar.ActionType,
                            AgentName = ar.AgentName,
                            Command = ar.Command,
                            Outcome = Enum.TryParse<ActionOutcome>(ar.Outcome, out var o) ? o : ActionOutcome.Unknown,
                            ExitCode = ar.ExitCode,
                            ErrorMessage = ar.ErrorMessage,
                            Duration = TimeSpan.TryParse(ar.Duration, out var dur) ? dur : TimeSpan.Zero,
                            StartedUtc = DateTime.TryParse(ar.StartedUtc, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var adt)
                                ? adt : DateTime.UtcNow,
                        };
                        session.AddResult(actionResult);

                        // Also populate per-agent summaries for dashboard rendering
                        session.TrackAgentAction(actionResult);
                    }

                    _history.Add(session);
                }

                while (_history.Count > MaxHistory)
                    _history.RemoveAt(_history.Count - 1);
            }
        }
        catch
        {
            // Restore failure is non-fatal; start fresh
        }
    }

    private static PersistedSession ToPersistedSession(ExecutionSession s) => new()
    {
        SessionId = s.SessionId,
        WatchItemTag = s.WatchItemTag,
        EventType = s.EventType,
        StartedUtc = s.StartedUtc.ToString("o"),
        CompletedUtc = s.CompletedUtc?.ToString("o"),
        State = s.State.ToString(),
        UserId = s.UserId,
        Source = s.Source,
        LockedAgents = s.LockedAgents,
        ResolvedParameters = new Dictionary<string, string>(s.ResolvedParameters),
        ActionResults = s.ActionResults.Select(a => new PersistedActionResult
        {
            ActionTag = a.ActionTag,
            ActionType = a.ActionType,
            AgentName = a.AgentName,
            Command = a.Command,
            Outcome = a.Outcome.ToString(),
            ExitCode = a.ExitCode,
            ErrorMessage = a.ErrorMessage,
            Duration = a.Duration.ToString(),
            StartedUtc = a.StartedUtc.ToString("o"),
            Sequence = a.Sequence,
        }).OrderBy(a => a.Sequence).ToArray(),
    };

    /// <summary>Flat DTO for JSON persistence of an execution session.</summary>
    public record PersistedSession
    {
        public string SessionId { get; init; } = "";
        public string WatchItemTag { get; init; } = "";
        public string EventType { get; init; } = "";
        public string StartedUtc { get; init; } = "";
        public string? CompletedUtc { get; init; }
        public string State { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Source { get; init; } = "";
        public string[] LockedAgents { get; init; } = [];
        public Dictionary<string, string> ResolvedParameters { get; init; } = new();
        public PersistedActionResult[] ActionResults { get; init; } = [];
    }

    /// <summary>Flat DTO for JSON persistence of an action result.</summary>
    public record PersistedActionResult
    {
        public string ActionTag { get; init; } = "";
        public string ActionType { get; init; } = "";
        public string? AgentName { get; init; }
        public string Command { get; init; } = "";
        public string Outcome { get; init; } = "";
        public int? ExitCode { get; init; }
        public string? ErrorMessage { get; init; }
        public string Duration { get; init; } = "";
        public string StartedUtc { get; init; } = "";
        public long Sequence { get; init; }
    }
}
