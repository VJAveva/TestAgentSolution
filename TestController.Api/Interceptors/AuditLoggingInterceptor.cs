using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Api.Interceptors;

/// <summary>
/// Wraps every RPC and writes an audit entry through IAuditWriter.
/// Fire-and-forget — does not slow down the request path.
/// </summary>
public sealed class AuditLoggingInterceptor
{
    private readonly IAuditWriter _auditWriter;

    public AuditLoggingInterceptor(IAuditWriter auditWriter)
    {
        _auditWriter = auditWriter;
    }

    /// <summary>
    /// Records an RPC invocation in the audit log.
    /// Called after authorization has resolved (so we have the decision).
    /// </summary>
    public void LogRpcCall(IUserContext user, string actionName, string? resourceId, bool allowed, string reasonCode)
    {
        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.GuestId is null ? user.UserId : null,
            GuestId = user.GuestId,
            RoleAtTime = user.Roles.Count > 0 && Enum.TryParse<Role>(user.Roles[0], true, out var r) ? r : null,
            ActionName = actionName,
            ResourceId = resourceId,
            Allowed = allowed,
            ReasonCode = reasonCode,
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
        });
    }
}
