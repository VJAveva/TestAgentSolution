namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The wire contract the agent puts in <c>ExecutionEvent.detail</c> for an EVENT_WINDOWS_UPDATE event, and the
/// controller reads back via <see cref="WindowsUpdateEventMapper"/>. Shared by both sides so the shape cannot drift.
/// Carries the complete level state on every message, never a delta (spec §16).
/// </summary>
public sealed record WindowsUpdatePayload
{
    public MaintenanceEventKind Kind { get; init; }
    public MaintenanceEventSource Source { get; init; }
    public bool RebootRequired { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<UpdateItemDto> Items { get; init; } = [];
    public DateTimeOffset? LastInstallUtc { get; init; }

    /// <summary>Left at default when the agent omits it; the controller then substitutes its receive time.</summary>
    public DateTimeOffset DetectedUtc { get; init; }
}
