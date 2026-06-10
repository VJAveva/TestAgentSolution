using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TestControllerGrpc.Identity;

namespace TestController.Persistence.Identity;

/// <summary>
/// Session lifecycle store: insert, lookup by token hash, revoke.
/// Singleton service that uses IDbContextFactory.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;

    public SessionStore(IDbContextFactory<OrchestratorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Session> CreateSessionAsync(string? userId, string? guestId, ClientKind clientKind, string? ipAddress, CancellationToken ct = default)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var tokenHash = SHA256.HashData(tokenBytes);

        var session = new Session
        {
            SessionId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            UserId = userId,
            GuestId = guestId,
            TokenHash = tokenHash,
            ClientKind = clientKind,
            IpAddress = ipAddress,
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow,
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        // Return a copy with plaintext token bytes for the caller
        session.TokenHash = tokenBytes;
        return session;
    }

    /// <summary>Returns the raw token as base64url for the caller to send to the client.</summary>
    public static string TokenToString(byte[] tokenBytes) => Convert.ToBase64String(tokenBytes)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] TokenFromString(string token)
    {
        var padded = token.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    public async Task<Session?> LookupByTokenAsync(string token, CancellationToken ct = default)
    {
        var tokenBytes = TokenFromString(token);
        var tokenHash = SHA256.HashData(tokenBytes);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash && s.RevokedUtc == null, ct);
    }

    public async Task TouchAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Sessions
            .Where(s => s.SessionId == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedUtc, DateTime.UtcNow), ct);
    }

    public async Task RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Sessions
            .Where(s => s.SessionId == sessionId && s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow), ct);
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow), ct);
    }

    public async Task RevokeAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Sessions
            .Where(s => s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow), ct);
    }
}

/// <summary>Session store interface for DI.</summary>
public interface ISessionStore
{
    Task<Session> CreateSessionAsync(string? userId, string? guestId, ClientKind clientKind, string? ipAddress, CancellationToken ct = default);
    Task<Session?> LookupByTokenAsync(string token, CancellationToken ct = default);
    Task TouchAsync(string sessionId, CancellationToken ct = default);
    Task RevokeAsync(string sessionId, CancellationToken ct = default);
    Task RevokeAllForUserAsync(string userId, CancellationToken ct = default);
    Task RevokeAllAsync(CancellationToken ct = default);
}
