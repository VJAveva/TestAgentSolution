using TestControllerGrpc.Core.Maintenance;

namespace TestController.Persistence.Maintenance;

/// <summary>
/// EF Core row for one maintenance operation (spec §6). A mutable persistence twin of the immutable domain record
/// <see cref="MaintenanceOperation"/>; the store maps between them. Ids are lowercase hyphenated GUID TEXT, matching
/// the rest of the schema. Log text lives in a file — the row only carries <see cref="LogPath"/>.
/// </summary>
public sealed class MaintenanceOperationRecord
{
    public string Id { get; set; } = "";
    public string NodeId { get; set; } = "";
    public MaintenanceKind Kind { get; set; }
    public string? SnapshotName { get; set; }
    public string? ScriptPath { get; set; }
    public MaintenanceOperationState State { get; set; }
    public RevertPhase Phase { get; set; }
    public MaintenanceTriggerSource TriggerSource { get; set; }
    public string? TriggeredBy { get; set; }
    public string? Reason { get; set; }

    /// <summary>Soft reference to the execution session that triggered a plan-driven revert (sessions are not in this DB).</summary>
    public string? LinkedRunId { get; set; }

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public int? ExitCode { get; set; }
    public RevertPhase? FailurePhase { get; set; }
    public string? LogPath { get; set; }
}
