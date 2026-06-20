using TestControllerGrpc.Locking;

namespace TestController.Api.Contracts;

/// <summary>
/// Maps domain PipelineLock to wire DTO.
/// </summary>
public static class LockMapper
{
    public static PipelineLockDto ToDto(PipelineLock lockEntry) => new()
    {
        PipelineId = lockEntry.PipelineId,
        OwnerUserId = lockEntry.Owner.UserId,
        OwnerDisplayName = lockEntry.Owner.DisplayName,
        OwnerClientKind = lockEntry.Owner.ClientKind.ToString(),
        AcquiredUtc = lockEntry.AcquiredUtc,
        ExpiresUtc = lockEntry.ExpiresUtc,
    };
}
