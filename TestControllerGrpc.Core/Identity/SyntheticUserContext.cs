namespace TestControllerGrpc.Identity;

/// <summary>
/// In-memory IUserContext implementation for synthetic/anonymous users.
/// Used in Default mode (DefaultUser.ForClient) and for Guest sessions.
/// Per 05_Default_Mode_Design.md §3.
/// </summary>
public sealed class SyntheticUserContext : IUserContext
{
    public string UserId { get; }
    public string DisplayName { get; }
    public ClientKind ClientKind { get; }
    public IReadOnlyList<string> Roles { get; }
    public IReadOnlySet<string> AssignedPipelineIds { get; }
    public string? GuestId { get; }

    public SyntheticUserContext(
        string userId,
        string displayName,
        ClientKind clientKind,
        IReadOnlyList<string> roles,
        IReadOnlySet<string>? assignedPipelineIds = null,
        string? guestId = null)
    {
        UserId = userId;
        DisplayName = displayName;
        ClientKind = clientKind;
        Roles = roles;
        AssignedPipelineIds = assignedPipelineIds ?? new HashSet<string>();
        GuestId = guestId;
    }
}
