using System.Collections.Concurrent;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Manages the lifecycle of execution sessions � tracks active and completed
/// pipeline runs, records per-action results, and supports retry of failed actions.
/// </summary>
public sealed class ExecutionSessionManager
{
    private readonly ConcurrentDictionary<string, ExecutionSession> _active = new();
    private readonly object _historyLock = new();
    private readonly List<ExecutionSession> _history = [];
    private const int MaxHistory = 50;

    private readonly IEventAggregator? _events;

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
        return cancelledTags;
    }
}
