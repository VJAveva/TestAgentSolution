using TestController.Api.Contracts;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Authorized retry operations for REST/gRPC paths.
/// Calls PipelineAuthorizationGuard with Pipeline_Retry → acquires pipeline lock → audit.
/// Per 02_Implementation_Roadmap.md §Phase 4.
/// Singleton DI per CONVENTIONS.md.
/// </summary>
public sealed class RetryService
{
    private readonly PipelineAuthorizationGuard _guard;
    private readonly ILockRegistry? _lockRegistry;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IAuditWriter _auditWriter;
    private readonly IAppLogger _logger;

    public RetryService(
        PipelineAuthorizationGuard guard,
        IAppLogger logger,
        IAuditWriter auditWriter,
        IVocabularyMonitor vocabMonitor,
        ILockRegistry? lockRegistry = null)
    {
        _guard = guard;
        _logger = logger;
        _auditWriter = auditWriter;
        _vocabMonitor = vocabMonitor;
        _lockRegistry = lockRegistry;
    }

    /// <summary>
    /// Authorize and acquire pipeline lock for a retry operation.
    /// Throws PipelineAuthorizationDeniedException on deny.
    /// Returns a conflict DTO if the pipeline is locked by another user.
    /// </summary>
    public async Task<PipelineLockDto?> AuthorizeRetryAsync(IUserContext user, string pipelineId, CancellationToken ct = default)
    {
        // Phase 6: reject retry on disabled pipeline
        var watchItem = _vocabMonitor.CurrentConfig?.WatchItems
            .FirstOrDefault(wi => string.Equals(wi.Tag, pipelineId, StringComparison.OrdinalIgnoreCase));
        if (watchItem is not null && !watchItem.IsEnabled)
        {
            throw new PipelineAuthorizationDeniedException(
                Permission.Pipeline_Retry, pipelineId, "pipeline-disabled", "Pipeline is disabled.");
        }

        await _guard.AuthorizeAsync(user, Permission.Pipeline_Retry, pipelineId, ct);
        _logger.Info("RetryService", $"Retry authorized for user={user.DisplayName} pipeline={pipelineId}");

        // Acquire pipeline lock (if registry available — controller host only)
        if (_lockRegistry is not null)
        {
            var owner = new OwnerIdentity(user.UserId, user.DisplayName, user.ClientKind);
            var result = _lockRegistry.TryAcquire(pipelineId, owner, LockKind.Trigger);

            switch (result)
            {
                case AcquireResult.Success:
                    _logger.Info("RetryService", $"Pipeline lock acquired for retry pipeline={pipelineId} owner={user.DisplayName}");
                    _auditWriter.Enqueue(new AuditEntry
                    {
                        UserId = user.UserId,
                        ActionName = "Pipeline_Retry",
                        ResourceId = pipelineId,
                        Allowed = true,
                        ReasonCode = "retry",
                        TimestampUtc = DateTime.UtcNow,
                        ClientKind = user.ClientKind,
                    });
                    break;

                case AcquireResult.Conflict conflict:
                    return LockMapper.ToDto(conflict.ExistingLock);
            }
        }
        else
        {
            // No lock registry (standalone WebApi) — still audit
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = user.UserId,
                ActionName = "Pipeline_Retry",
                ResourceId = pipelineId,
                Allowed = true,
                ReasonCode = "retry",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = user.ClientKind,
            });
        }

        return null; // No conflict
    }
}
