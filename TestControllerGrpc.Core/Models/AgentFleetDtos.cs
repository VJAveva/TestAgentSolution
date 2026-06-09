namespace TestControllerGrpc.Models;

/// <summary>
/// High-level status for an agent in the fleet panel.
/// Used by both the WPF UI and the SignalR WebClient payload.
/// </summary>
public enum AgentFleetStatus
{
    Idle,
    Waiting,
    Running,
    Rebooting,
    InstallFail,
    Error,
    Offline
}

/// <summary>
/// DTO representing a single agent's state in the fleet panel.
/// Immutable snapshot used for SignalR push and hub method responses.
/// </summary>
public sealed record AgentFleetDto(
    string AgentId,
    string DisplayName,
    AgentFleetStatus Status,
    string? CurrentTestSet,
    int? ProgressPercent,
    int? CompletedCount,
    int? TotalCount,
    string? StatusMessage,
    string? GroupKey,
    string? Owner);

/// <summary>
/// DTO representing a group of agents (one per active session, plus "Available" pool).
/// </summary>
public sealed record AgentFleetGroupDto(
    string GroupKey,
    string Title,
    string? Owner,
    bool IsAvailablePool,
    IReadOnlyList<AgentFleetDto> Agents);
