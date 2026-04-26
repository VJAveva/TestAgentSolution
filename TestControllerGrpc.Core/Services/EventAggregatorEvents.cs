using TestAgentGrpc;

namespace TestControllerGrpc.Services;

/// <summary>Fired when a remote agent registers itself via gRPC.</summary>
public sealed record AgentRegisteredEvent(string AgentName, string Address);

/// <summary>Fired when a remote agent unregisters via gRPC.</summary>
public sealed record AgentUnregisteredEvent(string AgentName);

/// <summary>Fired when a remote agent pushes a state change via gRPC.</summary>
public sealed record AgentStateChangedEvent(string AgentName, AgentState State);

/// <summary>Fired when a heartbeat with optional resource metrics arrives from an agent.</summary>
public sealed record AgentHeartbeatEvent(string AgentName, AgentState State, ResourceMetrics? Metrics);

/// <summary>Fired when a remote agent pushes an execution event via gRPC streaming.</summary>
public sealed record ExecutionEventReceivedEvent(ExecutionEvent Event);

/// <summary>Fired when a WatchItem execution starts (from WPF or WebApi).</summary>
public sealed record ExecutionStartedEvent(string SessionId, string WatchItemTag, string EventType, string Source);

/// <summary>Fired when a WatchItem execution completes (from WPF or WebApi).</summary>
public sealed record ExecutionCompletedEvent(string SessionId, string WatchItemTag, string State, int Passed, int Failed, int Total);

/// <summary>
/// Fired by pipeline executors as a node (Action / Group / Initialize / Ref)
/// transitions through its lifecycle. Status values are the same strings used
/// by the dashboard pills: "Pending" | "Running" | "Success" | "Failed" | "Skipped".
///
/// Replaces the 1-second polling adapter the dashboard previously used to
/// scrape <see cref="ExecutionSession.GetAgentSummaries"/>.
/// </summary>
public sealed record NodeProgressEvent(
    string SessionId,
    string AgentName,
    string NodeTag,
    string ActionType,
    string Command,
    string Status,
    int? ExitCode = null,
    string? ErrorMessage = null,
    string? Duration = null,
    int? ProgressPercent = null);

/// <summary>
/// Fired for each line of stdout/stderr produced by an agent during action
/// execution. <see cref="Kind"/> is "stdout" or "stderr".
/// </summary>
public sealed record AgentOutputEvent(
    DateTime Timestamp,
    string AgentName,
    string SessionId,
    string Kind,
    string Line);
