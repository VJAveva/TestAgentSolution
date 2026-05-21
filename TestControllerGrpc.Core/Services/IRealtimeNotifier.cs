using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Unified real-time notification contract for broadcasting events
/// to connected browser clients via SignalR.
///
/// Both WPF-hosted and standalone WebApi hosts implement this through
/// a shared <c>SignalRNotifier</c> that broadcasts to a single
/// <c>ControllerHub</c>. The React client subscribes to one hub at
/// <c>/hubs/controller</c> and receives all events with consistent names
/// regardless of deployment mode.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>Broadcast a pipeline log entry. Event: "LogEntry"</summary>
    Task NotifyLogEntry(PipelineLogEntry entry);

    /// <summary>Broadcast action/group node progress. Event: "ActionProgress"</summary>
    Task NotifyActionProgress(object payload);

    /// <summary>Broadcast agent stdout/stderr output. Event: "AgentOutput"</summary>
    Task NotifyAgentOutput(object payload);

    /// <summary>Broadcast agent status change. Event: "AgentStatusChanged"</summary>
    Task NotifyAgentStatusChanged(object payload);

    /// <summary>Broadcast batched agent heartbeats. Event: "AgentHeartbeats"</summary>
    Task NotifyAgentHeartbeats(IReadOnlyList<object> batch);

    /// <summary>Broadcast execution started. Event: "ExecutionStarted"</summary>
    Task NotifyExecutionStarted(ExecutionStartedEvent e);

    /// <summary>Broadcast execution completed. Event: "ExecutionCompleted"</summary>
    Task NotifyExecutionCompleted(ExecutionCompletedEvent e);

    /// <summary>Broadcast agent registered. Event: "AgentRegistered"</summary>
    Task NotifyAgentRegistered(AgentRegisteredEvent e);

    /// <summary>Broadcast agent unregistered. Event: "AgentUnregistered"</summary>
    Task NotifyAgentUnregistered(AgentUnregisteredEvent e);

    /// <summary>Broadcast WatchList reloaded. Event: "WatchListReloaded"</summary>
    Task NotifyWatchListReloaded();

    /// <summary>Broadcast agent lock state changed. Event: "AgentLocksChanged"</summary>
    Task NotifyAgentLocksChanged(AgentLocksChangedEvent e);

    /// <summary>Broadcast single agent fleet card update. Event: "FleetAgentUpdated"</summary>
    Task NotifyFleetAgentUpdated(AgentFleetDto agent);

    /// <summary>Broadcast agent removal from fleet panel. Event: "FleetAgentRemoved"</summary>
    Task NotifyFleetAgentRemoved(string agentId);

    /// <summary>Broadcast full fleet snapshot. Event: "FleetSnapshot"</summary>
    Task NotifyFleetSnapshot(IReadOnlyList<AgentFleetGroupDto> groups);
}
