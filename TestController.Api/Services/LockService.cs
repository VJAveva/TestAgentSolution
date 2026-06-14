using TestController.Api.Contracts;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Lock operations exposed to clients via REST (WebApi proxy) or direct gRPC.
/// Uses ILockRegistry — available only when running in the WPF controller host.
/// </summary>
public sealed class LockService
{
    private readonly ILockRegistry _lockRegistry;
    private readonly IAuthorizationService _authorizationService;
    private readonly IAuditWriter _auditWriter;
    private readonly IAppLogger _logger;

    public LockService(
        ILockRegistry lockRegistry,
        IAuthorizationService authorizationService,
        IAuditWriter auditWriter,
        IAppLogger logger)
    {
        _lockRegistry = lockRegistry;
        _authorizationService = authorizationService;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public IReadOnlyList<PipelineLockDto> ListLocks()
    {
        return _lockRegistry.GetAll().Select(LockMapper.ToDto).ToList();
    }

    public PipelineLockDto? GetLock(string pipelineId)
    {
        var lockEntry = _lockRegistry.Get(pipelineId);
        return lockEntry is null ? null : LockMapper.ToDto(lockEntry);
    }

    public bool ReleaseLock(string pipelineId, IUserContext user)
    {
        var owner = new OwnerIdentity(user.UserId, user.DisplayName, user.ClientKind);
        var released = _lockRegistry.TryRelease(pipelineId, owner);

        if (released)
        {
            _logger.Info("Lock", $"Lock released on pipeline={pipelineId} by user={user.DisplayName}");
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = user.UserId,
                ActionName = "Pipeline_LockRelease",
                ResourceId = pipelineId,
                Allowed = true,
                ReasonCode = "owner-release",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = user.ClientKind,
            });
        }

        return released;
    }

    /// <summary>
    /// Force-release a lock. Requires Pipeline_ForceRelease permission.
    /// Reason is stored in audit only — never logged via AppLogger.
    /// </summary>
    public async Task<(bool Success, PipelineLockDto? ConflictLock, string? Error)> ForceReleaseAsync(
        string pipelineId, string reason, IUserContext user, CancellationToken ct = default)
    {
        // Server-side validation even though UI gates it
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            return (false, null, "Reason must be at least 10 characters long");

        var decision = await _authorizationService.CanAsync(user, Permission.Pipeline_ForceRelease, pipelineId, ct);
        if (!decision.Allowed)
            return (false, null, decision.HumanReadable ?? "Permission denied");

        var existing = _lockRegistry.Get(pipelineId);
        if (existing is null)
            return (false, null, "No active lock on this pipeline");

        var priorOwner = existing.Owner;
        _lockRegistry.ForceRelease(pipelineId);

        // Operational log without reason text (may be sensitive)
        _logger.Info("Lock", $"Force-release on pipeline={pipelineId} by user={user.UserId}");

        // Audit with full details (reason + prior owner) — fire-and-forget
        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.UserId,
            ActionName = "Pipeline_ForceRelease",
            ResourceId = pipelineId,
            Allowed = true,
            ReasonCode = "force-release",
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
            CorrelationId = $"reason:{reason}|prior_owner:{priorOwner.UserId}:{priorOwner.DisplayName}",
        });

        return (true, null, null);
    }
}
