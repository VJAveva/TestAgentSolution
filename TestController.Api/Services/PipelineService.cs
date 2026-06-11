using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Authorized pipeline operations for REST/gRPC paths.
/// Calls PipelineAuthorizationGuard → delegates to existing execution infrastructure.
/// Singleton DI per CONVENTIONS.md.
/// Per 02_Implementation_Roadmap.md §Phase 2.
/// </summary>
public sealed class PipelineService
{
    private readonly PipelineAuthorizationGuard _guard;
    private readonly IAppLogger _logger;

    public PipelineService(PipelineAuthorizationGuard guard, IAppLogger logger)
    {
        _guard = guard;
        _logger = logger;
    }

    /// <summary>
    /// Authorize a trigger operation. Throws PipelineAuthorizationDeniedException on deny.
    /// The caller is responsible for actually starting the execution after this returns.
    /// </summary>
    public async Task AuthorizeTriggerAsync(IUserContext user, string pipelineId, CancellationToken ct = default)
    {
        await _guard.AuthorizeAsync(user, Permission.Pipeline_Trigger, pipelineId, ct);
        _logger.Info("PipelineService", $"Trigger authorized for user={user.DisplayName} pipeline={pipelineId}");
    }

    /// <summary>
    /// Authorize a cancel operation. Throws PipelineAuthorizationDeniedException on deny.
    /// Phase 2a: Pipeline_Cancel for both own and others' runs (split in Phase 3).
    /// </summary>
    public async Task AuthorizeCancelAsync(IUserContext user, string pipelineId, CancellationToken ct = default)
    {
        await _guard.AuthorizeAsync(user, Permission.Pipeline_Cancel, pipelineId, ct);
        _logger.Info("PipelineService", $"Cancel authorized for user={user.DisplayName} pipeline={pipelineId}");
    }
}
