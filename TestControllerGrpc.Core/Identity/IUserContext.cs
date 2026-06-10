namespace TestControllerGrpc.Identity;

/// <summary>
/// Identity flowing through every request. Hydrated by SessionAuthInterceptor
/// and consumed by IAuthorizationService and RPC handlers.
/// </summary>
public interface IUserContext
{
    string UserId { get; }
    string DisplayName { get; }
    ClientKind ClientKind { get; }
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    /// Pre-fetched pipeline IDs assigned to this user (for Engineer role).
    /// Empty for Admin/SrMgr (who have implicit access to all) and Guest.
    /// </summary>
    IReadOnlySet<string> AssignedPipelineIds { get; }

    /// <summary>Null for authenticated users; non-null for Guest sessions.</summary>
    string? GuestId { get; }
}
