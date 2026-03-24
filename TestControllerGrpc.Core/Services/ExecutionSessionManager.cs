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

    /// <summary>Begins a new execution session and tracks it as active.</summary>
    public ExecutionSession BeginSession(
        string watchItemTag, string eventType,
        Dictionary<string, string> resolvedParameters,
        List<IActionNode> snapshotNodes)
    {
        var session = new ExecutionSession
        {
            WatchItemTag = watchItemTag,
            EventType = eventType,
            ResolvedParameters = new Dictionary<string, string>(resolvedParameters, StringComparer.OrdinalIgnoreCase),
            SnapshotNodes = snapshotNodes
        };
        _active[session.SessionId] = session;
        return session;
    }

    /// <summary>Records a per-action result into the active session.</summary>
    public void RecordResult(string sessionId, ActionExecutionResult result)
    {
        if (_active.TryGetValue(sessionId, out var session))
            session.ActionResults.Add(result);
    }

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

    /// <summary>Cancels all active sessions, moving them to history as cancelled.</summary>
    public List<string> CancelAll()
    {
        var cancelledTags = new List<string>();
        var ids = _active.Keys.ToList();
        foreach (var id in ids)
        {
            if (_active.TryRemove(id, out var session))
            {
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
