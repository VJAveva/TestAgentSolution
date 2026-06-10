using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Identity;

namespace TestController.Api.Services;

/// <summary>
/// Auth service handling Login, Logout, ChangePassword RPCs.
/// Per 01_System_Design.md §4.1.
/// </summary>
public sealed class AuthService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly ISessionStore _sessionStore;
    private readonly PasswordHasher _passwordHasher;

    public AuthService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        ISessionStore sessionStore,
        PasswordHasher passwordHasher)
    {
        _dbFactory = dbFactory;
        _sessionStore = sessionStore;
        _passwordHasher = passwordHasher;
    }

    public sealed record LoginResult(bool Success, string? Token = null, Role? Role = null, bool MustChangePassword = false, string? Error = null);

    public async Task<LoginResult> LoginAsync(string username, string password, ClientKind clientKind, string? ipAddress, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);

        if (user is null)
            return new LoginResult(false, Error: "Invalid username or password");

        if (!_passwordHasher.Verify(password, user.PasswordHash))
            return new LoginResult(false, Error: "Invalid username or password");

        var session = await _sessionStore.CreateSessionAsync(user.UserId, null, clientKind, ipAddress, ct);
        var token = SessionStore.TokenToString(session.TokenHash);

        return new LoginResult(true, token, user.Role, user.MustChangePassword);
    }

    public async Task<LoginResult> LoginAsGuestAsync(ClientKind clientKind, string? ipAddress, CancellationToken ct = default)
    {
        var guestId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var session = await _sessionStore.CreateSessionAsync(null, guestId, clientKind, ipAddress, ct);
        var token = SessionStore.TokenToString(session.TokenHash);

        return new LoginResult(true, token, Role.Guest);
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        var session = await _sessionStore.LookupByTokenAsync(token, ct);
        if (session is not null)
        {
            await _sessionStore.RevokeAsync(session.SessionId, ct);
        }
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);

        if (user is null)
            return (false, "User not found");

        if (!_passwordHasher.Verify(currentPassword, user.PasswordHash))
            return (false, "Current password is incorrect");

        user.PasswordHash = _passwordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);

        // Revoke all existing sessions to force re-login with new password
        await _sessionStore.RevokeAllForUserAsync(userId, ct);

        return (true, null);
    }
}
