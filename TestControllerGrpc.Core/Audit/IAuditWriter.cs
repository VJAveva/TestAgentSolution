using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Authorization;

/// <summary>
/// Fire-and-forget audit writer interface. Implementations use a bounded channel
/// and background drain — never await in the request path.
/// Per 01_System_Design.md §11.
/// </summary>
public interface IAuditWriter
{
    void Enqueue(AuditEntry entry);
}
