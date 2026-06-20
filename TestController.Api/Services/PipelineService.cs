using TestController.Api.Contracts;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Authorized pipeline operations for REST/gRPC paths.
/// Calls PipelineAuthorizationGuard → delegates to existing execution infrastructure.
/// Phase 3a: acquires pipeline lock before trigger proceeds.
/// Singleton DI per CONVENTIONS.md.
/// Per 02_Implementation_Roadmap.md §Phase 2 + phase-3a-context.md.
/// </summary>
public sealed class PipelineService
{
    private readonly PipelineAuthorizationGuard _guard;
    private readonly ILockRegistry? _lockRegistry;
    private readonly IVocabularyMonitor? _vocabMonitor;
    private readonly IAuditWriter _auditWriter;
    private readonly IAppLogger _logger;

    public PipelineService(
        PipelineAuthorizationGuard guard,
        IAppLogger logger,
        IAuditWriter auditWriter,
        IVocabularyMonitor? vocabMonitor = null,
        ILockRegistry? lockRegistry = null)
    {
        _guard = guard;
        _logger = logger;
        _auditWriter = auditWriter;
        _vocabMonitor = vocabMonitor;
        _lockRegistry = lockRegistry;
    }

    /// <summary>
    /// Authorize and acquire pipeline lock for a trigger operation.
    /// Throws PipelineAuthorizationDeniedException on deny.
    /// Returns a conflict DTO if the pipeline is locked by another user.
    /// </summary>
    public async Task<PipelineLockDto?> AuthorizeTriggerAsync(IUserContext user, string pipelineId, CancellationToken ct = default)
    {
        // Phase 6: reject trigger on disabled pipeline
        var watchItem = _vocabMonitor?.CurrentConfig?.WatchItems
            .FirstOrDefault(wi => string.Equals(wi.Tag, pipelineId, StringComparison.OrdinalIgnoreCase));
        if (watchItem is not null && !watchItem.IsEnabled)
        {
            throw new PipelineAuthorizationDeniedException(
                Permission.Pipeline_Trigger, pipelineId, "pipeline-disabled", "Pipeline is disabled.");
        }

        await _guard.AuthorizeAsync(user, Permission.Pipeline_Trigger, pipelineId, ct);
        _logger.Info("PipelineService", $"Trigger authorized for user={user.DisplayName} pipeline={pipelineId}");

        // Phase 3a: Acquire pipeline lock (if registry available — controller host only)
        if (_lockRegistry is not null)
        {
            var owner = new OwnerIdentity(user.UserId, user.DisplayName, user.ClientKind);
            var result = _lockRegistry.TryAcquire(pipelineId, owner, LockKind.Trigger);

            switch (result)
            {
                case AcquireResult.Success:
                    _logger.Info("PipelineService", $"Pipeline lock acquired for pipeline={pipelineId} owner={user.DisplayName}");
                    _auditWriter.Enqueue(new AuditEntry
                    {
                        UserId = user.UserId,
                        ActionName = "Pipeline_LockAcquire",
                        ResourceId = pipelineId,
                        Allowed = true,
                        ReasonCode = "trigger",
                        TimestampUtc = DateTime.UtcNow,
                        ClientKind = user.ClientKind,
                    });
                    break;

                case AcquireResult.Conflict conflict:
                    return LockMapper.ToDto(conflict.ExistingLock);
            }
        }

        return null; // No conflict
    }

    /// <summary>
    /// Release the pipeline lock after execution completes (any terminal state).
    /// Called from execution completion hooks.
    /// </summary>
    public void ReleaseLockOnCompletion(string pipelineId, IUserContext user)
    {
        if (_lockRegistry is null) return;

        var existing = _lockRegistry.Get(pipelineId);
        if (existing is null) return;

        var owner = new OwnerIdentity(user.UserId, user.DisplayName, user.ClientKind);
        if (existing.Owner != owner) return;

        if (_lockRegistry.TryRelease(pipelineId, existing.Token))
        {
            _logger.Info("PipelineService", $"Pipeline lock released on completion for pipeline={pipelineId}");
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = user.UserId,
                ActionName = "Pipeline_LockRelease",
                ResourceId = pipelineId,
                Allowed = true,
                ReasonCode = "completion",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = user.ClientKind,
            });
        }
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
