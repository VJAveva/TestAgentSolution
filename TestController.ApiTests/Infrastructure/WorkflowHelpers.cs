using System.Diagnostics;

namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Polling helpers for the asynchronous execution lifecycle. Triggering is
/// fire-and-forget (202 Accepted), so workflow tests must poll the session until it
/// reaches a terminal state.
/// </summary>
public static class WorkflowHelpers
{
    /// <summary>
    /// Polls GET /api/execution/{id} until the session reaches a terminal state
    /// (Completed | PartialFailure | Failed) or the timeout elapses. Returns the
    /// last observed status (may be null if the session vanished / 404'd).
    /// </summary>
    public static async Task<SessionStatus?> PollUntilTerminalAsync(
        ApiClient api,
        string sessionId,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var limit = timeout ?? TestConfig.DefaultTimeout;
        var step = interval ?? TimeSpan.FromMilliseconds(150);
        var sw = Stopwatch.StartNew();

        SessionStatus? last = null;
        while (sw.Elapsed < limit)
        {
            last = await api.GetSessionAsync(sessionId);
            if (last is not null && SessionStates.IsTerminal(last.State))
                return last;
            await Task.Delay(step);
        }
        return last;
    }

    /// <summary>Waits until the session appears in the active sessions list (or times out).</summary>
    public static async Task<bool> WaitUntilActiveAsync(
        ApiClient api,
        string sessionId,
        TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            var sessions = await api.GetSessionsAsync();
            if (sessions.Sessions.Exists(s =>
                    string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                return true;
            await Task.Delay(100);
        }
        return false;
    }
}
