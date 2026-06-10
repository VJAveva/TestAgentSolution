using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.SystemMode;

/// <summary>
/// Atomic mode transitions per 05_Default_Mode_Design.md §8 + §9.
/// All mutations in a single transaction — no partial state on crash.
/// </summary>
public sealed class RbacModeTransitionService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly IWritableOptions<RbacOptions> _writableOptions;
    private readonly ISessionStore _sessionStore;
    private readonly PasswordHasher _passwordHasher;
    private readonly IAuditWriter _auditWriter;

    public RbacModeTransitionService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        IWritableOptions<RbacOptions> writableOptions,
        ISessionStore sessionStore,
        PasswordHasher passwordHasher,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _writableOptions = writableOptions;
        _sessionStore = sessionStore;
        _passwordHasher = passwordHasher;
        _auditWriter = auditWriter;
    }

    /// <summary>
    /// Switch from Default to Secured mode. Creates the initial Administrator.
    /// Per 05_Default_Mode_Design.md §8.
    /// </summary>
    public async Task<(bool Success, string? Error)> SwitchToSecuredAsync(
        string adminUsername, string adminEmail, string adminPassword, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Check if already in Secured mode
            if (_writableOptions.Value.Enabled)
                return (true, null); // Idempotent

            // Check for existing admin (recovery path per §8.3)
            var existingAdmin = await db.Users.FirstOrDefaultAsync(
                u => u.Role == Role.Administrator && u.IsActive, ct);

            if (existingAdmin is null)
            {
                // Check for username collision
                if (await db.Users.AnyAsync(u => u.Username == adminUsername, ct))
                    return (false, $"Username '{adminUsername}' already exists");

                var admin = new User
                {
                    UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                    Username = adminUsername,
                    Email = adminEmail,
                    Role = Role.Administrator,
                    PasswordHash = _passwordHasher.Hash(adminPassword),
                    MustChangePassword = false,
                    IsActive = true,
                    CreatedUtc = DateTime.UtcNow,
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync(ct);
            }

            // Flip the flag
            _writableOptions.Update(opts => opts.Enabled = true);

            // Audit
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = DefaultUser.UserId.ToString("D"),
                ActionName = "System_SwitchToSecured",
                Allowed = true,
                ReasonCode = "mode-transition",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = ClientKind.Wpf,
            });

            await transaction.CommitAsync(ct);
            return (true, null);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Switch from Secured to Default mode. Revokes all sessions, archives users.
    /// Per 05_Default_Mode_Design.md §9.
    /// </summary>
    public async Task<(bool Success, string? Error)> SwitchToDefaultAsync(
        IUserContext actor, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Check if already in Default mode
            if (!_writableOptions.Value.Enabled)
                return (true, null); // Idempotent

            // Revoke all sessions
            await _sessionStore.RevokeAllAsync(ct);

            // Archive all users (set IsActive = false)
            await db.Users
                .Where(u => u.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), ct);

            // Flip the flag
            _writableOptions.Update(opts => opts.Enabled = false);

            // Audit (last entry with real identity)
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = actor.UserId,
                ActionName = "System_SwitchToDefault",
                Allowed = true,
                ReasonCode = "mode-transition",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = actor.ClientKind,
            });

            await transaction.CommitAsync(ct);
            return (true, null);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
