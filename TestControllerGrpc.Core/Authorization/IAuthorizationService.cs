using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Authorization;

/// <summary>
/// Single authorization API consumed by all RPC handlers.
/// Per 01_System_Design.md §3.2 — default-deny, admin shortcut,
/// assignment-aware for pipeline-scoped permissions.
/// </summary>
public interface IAuthorizationService
{
    Task<AuthDecision> CanAsync(
        IUserContext user,
        Permission permission,
        string? resourceId = null,
        CancellationToken ct = default);
}
