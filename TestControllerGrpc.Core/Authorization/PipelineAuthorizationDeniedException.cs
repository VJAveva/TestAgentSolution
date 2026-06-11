using TestControllerGrpc.Authorization;

namespace TestControllerGrpc.Authorization;

/// <summary>
/// Thrown by PipelineAuthorizationGuard when IAuthorizationService.CanAsync denies.
/// Transport-agnostic: callers convert to PermissionDenied (gRPC) or 403 (REST).
/// Per 02_Implementation_Roadmap.md §Phase 2 exit criteria.
/// </summary>
public sealed class PipelineAuthorizationDeniedException : Exception
{
    public Permission Permission { get; }
    public string? ResourceId { get; }
    public string ReasonCode { get; }

    public PipelineAuthorizationDeniedException(
        Permission permission,
        string? resourceId,
        string reasonCode,
        string? humanReadable)
        : base(humanReadable ?? $"Authorization denied: {permission} on {resourceId ?? "(global)"} — {reasonCode}")
    {
        Permission = permission;
        ResourceId = resourceId;
        ReasonCode = reasonCode;
    }
}
