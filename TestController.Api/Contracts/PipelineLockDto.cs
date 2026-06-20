using TestControllerGrpc.Identity;

namespace TestController.Api.Contracts;

/// <summary>
/// Wire DTO for pipeline lock state. Carries enough info for conflict dialogs (Mockup 6)
/// without a second round-trip. Per 03_Integration_With_Lock_Spec.md §4.3.
/// </summary>
public sealed record PipelineLockDto
{
    public required string PipelineId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string OwnerDisplayName { get; init; }
    public required string OwnerClientKind { get; init; }
    public required DateTime AcquiredUtc { get; init; }
    public required DateTime ExpiresUtc { get; init; }
}
