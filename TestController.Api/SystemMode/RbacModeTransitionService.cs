using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;

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
    private readonly ILockRegistry? _lockRegistry;

    public RbacModeTransitionService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        IWritableOptions<RbacOptions> writableOptions,
        ISessionStore sessionStore,
        PasswordHasher passwordHasher,
        IAuditWriter auditWriter,
        ILockRegistry? lockRegistry = null)
    {
        _dbFactory = dbFactory;
        _writableOptions = writableOptions;
        _sessionStore = sessionStore;
        _passwordHasher = passwordHasher;
        _auditWriter = auditWriter;
        _lockRegistry = lockRegistry;
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

            // Check for existing admin (active OR archived — prevents duplicate creation)
            var existingAdmin = await db.Users.FirstOrDefaultAsync(
                u => u.Role == Role.Administrator, ct);

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
                existingAdmin = admin;
            }
            else if (!existingAdmin.IsActive)
            {
                // Reactivate all archived users (symmetric with SwitchToDefault archive)
                await db.Users
                    .Where(u => !u.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true), ct);
            }

            // Flip the flag
            _writableOptions.Update(opts => opts.Enabled = true);

            // Phase 3a: Rewrite lock owners to the new Admin (per 05_Default_Mode_Design.md §8.2)
            if (_lockRegistry is not null && existingAdmin is not null)
            {
                var adminOwner = new OwnerIdentity(existingAdmin.UserId, existingAdmin.Username, ClientKind.Wpf);
                _lockRegistry.RewriteOwners(adminOwner);
            }

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
    /// Checks whether any Administrator account exists in the database (active or archived).
    /// Used by the WPF client to decide whether to show the wizard or the reactivation path.
    /// </summary>
    public async Task<bool> HasExistingAdminAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(u => u.Role == Role.Administrator, ct);
    }

    /// <summary>
    /// Switch to Secured mode by reactivating existing archived users.
    /// Called when an admin already exists (skip wizard). Symmetric inverse of SwitchToDefaultAsync.
    /// </summary>
    public async Task<(bool Success, string? Error)> SwitchToSecuredReactivateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Idempotency
            if (_writableOptions.Value.Enabled)
                return (true, null);

            // Safety: must have at least one admin to reactivate
            var admin = await db.Users.FirstOrDefaultAsync(
                u => u.Role == Role.Administrator, ct);
            if (admin is null)
                return (false, "No administrator account exists. Use the initial setup wizard.");

            // Reactivate ALL previously-archived users in a single statement
            await db.Users
                .Where(u => !u.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true), ct);

            // Flip the flag
            _writableOptions.Update(opts => opts.Enabled = true);

            // Phase 3a: Rewrite lock owners to the admin
            if (_lockRegistry is not null)
            {
                var adminOwner = new OwnerIdentity(admin.UserId, admin.Username, ClientKind.Wpf);
                _lockRegistry.RewriteOwners(adminOwner);
            }

            // Audit
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = DefaultUser.UserId.ToString("D"),
                ActionName = "System_SwitchToSecured",
                Allowed = true,
                ReasonCode = "mode-transition-reactivate",
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
    /// All writes use a SINGLE DbContext/connection to avoid SQLite lock contention.
    /// </summary>
    public async Task<(bool Success, string? Error)> SwitchToDefaultAsync(
        IUserContext actor, CancellationToken ct = default)
    {
        // Retry up to 3 times on transient SQLite locked errors (Error 5)
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SwitchToDefaultCoreAsync(actor, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries)
            {
                // SQLITE_BUSY — wait and retry
                await Task.Delay(200 * (attempt + 1), ct);
            }
        }
    }

    private async Task<(bool Success, string? Error)> SwitchToDefaultCoreAsync(
        IUserContext actor, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Check if already in Default mode
            if (!_writableOptions.Value.Enabled)
                return (true, null); // Idempotent

            // Revoke all sessions ON THE SAME connection (avoids second-connection lock)
            await db.Sessions
                .Where(s => s.RevokedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow), ct);

            // Archive all users (set IsActive = false)
            await db.Users
                .Where(u => u.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), ct);

            // Flip the flag
            _writableOptions.Update(opts => opts.Enabled = false);

            // Phase 3a: Rewrite lock owners to Default user (per 05_Default_Mode_Design.md §9.1)
            _lockRegistry?.RewriteOwners(
                new OwnerIdentity(DefaultUser.UserId.ToString("D"), "Default user", ClientKind.Wpf));

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
