namespace TestControllerGrpc.Identity;

/// <summary>
/// Synthetic Default user injected when RBAC:Enabled = false.
/// Stable UUID so audit entries correlate across restarts.
/// Per 05_Default_Mode_Design.md §3.
/// </summary>
public static class DefaultUser
{
    public static readonly Guid UserId = new("00000000-0000-0000-0000-000000000001");

    public static IUserContext ForClient(ClientKind kind) => new SyntheticUserContext(
        userId: UserId.ToString("D"),
        displayName: "Default user",
        clientKind: kind,
        roles: []);
}
