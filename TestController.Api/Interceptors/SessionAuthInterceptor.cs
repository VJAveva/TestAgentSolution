using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.Interceptors;

/// <summary>
/// gRPC interceptor that hydrates IUserContext into the call context.
/// Per 05_Default_Mode_Design.md §12 — reads IOptionsMonitor on every call.
/// Default mode: injects synthetic Default user (no token required).
/// Secured mode: validates bearer token against Sessions table.
/// </summary>
public sealed class SessionAuthInterceptor
{
    private readonly IOptionsMonitor<RbacOptions> _options;
    private readonly ISessionStore _sessionStore;
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;

    public SessionAuthInterceptor(
        IOptionsMonitor<RbacOptions> options,
        ISessionStore sessionStore,
        IDbContextFactory<OrchestratorDbContext> dbFactory)
    {
        _options = options;
        _sessionStore = sessionStore;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Resolves IUserContext from the request headers/metadata.
    /// Returns null only if in Secured mode and no valid token is present.
    /// </summary>
    public async Task<IUserContext?> ResolveUserAsync(
        string? authorizationHeader,
        ClientKind clientKind,
        CancellationToken ct = default)
    {
        // Default mode: inject synthetic user with no authentication required
        if (!_options.CurrentValue.Enabled)
        {
            return DefaultUser.ForClient(clientKind);
        }

        // Secured mode: require bearer token
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;

        var token = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..].Trim()
            : authorizationHeader.Trim();

        if (string.IsNullOrEmpty(token))
            return null;

        var session = await _sessionStore.LookupByTokenAsync(token, ct);
        if (session is null)
            return null;

        // Check inactivity timeout (60 minutes default)
        if ((DateTime.UtcNow - session.LastUsedUtc).TotalMinutes > 60)
            return null;

        // Check absolute TTL for guest sessions (60 minutes from creation)
        if (session.GuestId is not null && (DateTime.UtcNow - session.CreatedUtc).TotalMinutes > 60)
            return null;

        // Touch session (fire-and-forget)
        _ = _sessionStore.TouchAsync(session.SessionId, CancellationToken.None);

        // Hydrate user context
        if (session.UserId is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == session.UserId && u.IsActive, ct);
            if (user is null)
                return null;

            var assignments = await db.PipelineAssignments
                .Where(pa => pa.UserId == user.UserId)
                .Select(pa => pa.PipelineId)
                .ToListAsync(ct);

            return new SyntheticUserContext(
                userId: user.UserId,
                displayName: user.Username,
                clientKind: session.ClientKind,
                roles: [user.Role.ToString()],
                assignedPipelineIds: new HashSet<string>(assignments));
        }

        // Guest session
        return new SyntheticUserContext(
            userId: session.GuestId!,
            displayName: "Guest",
            clientKind: session.ClientKind,
            roles: [Role.Guest.ToString()],
            guestId: session.GuestId);
    }
}
