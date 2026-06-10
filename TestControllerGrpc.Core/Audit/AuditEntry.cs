using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Authorization;

/// <summary>
/// One row of the audit log. Written by IAuditWriter on every authorization decision.
/// Per 01_System_Design.md §7.1 AuditEntries table.
/// </summary>
public sealed class AuditEntry
{
    public long AuditId { get; set; }
    public string? UserId { get; set; }
    public string? GuestId { get; set; }
    public Role? RoleAtTime { get; set; }
    public string ActionName { get; set; } = "";
    public string? ResourceId { get; set; }
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public ClientKind ClientKind { get; set; }
    public string? CorrelationId { get; set; }
}
