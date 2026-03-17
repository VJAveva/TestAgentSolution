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
