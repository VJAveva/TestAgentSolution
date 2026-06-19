namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// DTOs that mirror the REAL JSON shapes returned by TestController.WebApi.
/// These were verified against the actual controller/endpoint source — they are
/// intentionally NOT a 1:1 copy of the original spec, which assumed shapes that
/// do not exist. See ApiClient for the routes these map to.
/// </summary>

/// <summary>
/// Body for POST /api/execution/trigger/{tag}. All fields optional; the endpoint
/// accepts an empty body (EmptyBodyBehavior=Allow).
/// </summary>
public sealed record TriggerRequest(
    string? BuildNumber = null,
    string? DropLocation = null,
    Dictionary<string, string>? Parameters = null,
    string? UserId = null,
    long? LockVersion = null);

/// <summary>
/// 202 Accepted response from POST /api/execution/trigger/{tag}.
/// NOTE: the trigger does NOT return session state — only the id and a message.
/// To inspect state you must GET the session afterwards.
/// </summary>
public sealed record TriggerResponse(string SessionId, string Message);

/// <summary>
/// Single-session DTO from GET /api/execution/{sessionId} (ToSessionDto).
/// `State` is the raw <c>SessionState</c> enum name: Running | Completed |
/// PartialFailure | Failed. There is no "Cancelled" state.
/// </summary>
public sealed record SessionStatus(
    string SessionId,
    string WatchItemTag,
    string? EventType,
    string? StartedUtc,
    string? CompletedUtc,
    string State,
    string? UserId,
    string? Source,
    string[]? LockedAgents,
    int TotalActions,
    int SucceededCount,
    int FailedCount,
    string? Summary);

/// <summary>One entry from the GET /api/execution/sessions list (carries progress%).</summary>
public sealed record SessionListItem(
    string SessionId,
    string WatchItemTag,
    string? EventType,
    string State,
    string? StartedUtc,
    int TotalActions,
    int CompletedActions,
    int PassedActions,
    int FailedActions,
    double ProgressPercent);

/// <summary>Envelope returned by GET /api/execution/sessions.</summary>
public sealed record SessionsEnvelope(
    int ActiveCount,
    bool HasActive,
    List<SessionListItem> Sessions);

/// <summary>Envelope returned by GET /api/execution/status.</summary>
public sealed record ExecutionStatus(bool IsExecuting, int ActiveCount);

/// <summary>200 response from POST /api/execution/{sessionId}/cancel.</summary>
public sealed record CancelResponse(string Message, string? CancelledBy);

/// <summary>One element of the array returned by GET /api/agents.</summary>
public sealed record AgentInfo(
    string Name,
    string? Address,
    string Status,
    string? LastCheckedUtc);

/// <summary>The real <c>SessionState</c> enum names, as serialized into DTO `state`.</summary>
public static class SessionStates
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string PartialFailure = "PartialFailure";
    public const string Failed = "Failed";

    public static readonly string[] Terminal = { Completed, PartialFailure, Failed };

    public static bool IsTerminal(string? state) =>
        state is not null && Array.Exists(Terminal, s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
}
