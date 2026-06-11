using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Api.Services;

/// <summary>
/// Wraps IAuthorizationService.CanAsync for pipeline operations.
/// On Allow → returns silently. On Deny → throws PipelineAuthorizationDeniedException.
/// Does NOT double-audit (IAuthorizationService already enqueues audit per §3.2 rule 6).
/// Singleton DI per CONVENTIONS.md.
/// </summary>
public sealed class PipelineAuthorizationGuard
{
    private readonly IAuthorizationService _authz;

    public PipelineAuthorizationGuard(IAuthorizationService authz)
    {
        _authz = authz;
    }

    /// <summary>
    /// Authorize a pipeline operation. Returns on Allow; throws on Deny.
    /// </summary>
    public async Task AuthorizeAsync(
        IUserContext user,
        Permission permission,
        string? resourceId = null,
        CancellationToken ct = default)
    {
        var decision = await _authz.CanAsync(user, permission, resourceId, ct);
        if (!decision.Allowed)
        {
            throw new PipelineAuthorizationDeniedException(
                permission, resourceId, decision.ReasonCode, decision.HumanReadable);
        }
    }
}
