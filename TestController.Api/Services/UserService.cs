using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestController.Persistence.Identity;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Api.Services;

/// <summary>
/// User CRUD + pipeline assignment service. Admin-only operations.
/// Singleton using IDbContextFactory. Per 02_Implementation_Roadmap.md §Phase 1.
/// </summary>
public sealed class UserService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly PasswordHasher _passwordHasher;
    private readonly ISessionStore _sessionStore;
    private readonly IAuditWriter _auditWriter;

    public UserService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        PasswordHasher passwordHasher,
        ISessionStore sessionStore,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _passwordHasher = passwordHasher;
        _sessionStore = sessionStore;
        _auditWriter = auditWriter;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    public sealed record UserDto(
        string UserId, string Username, string Email, string Role,
        bool IsActive, bool MustChangePassword, DateTime CreatedUtc,
        string? CreatedByUserId, int PipelineCount);

    public sealed record CreateUserRequest(string Username, string Email, string Role, string? Password);
    public sealed record UpdateUserRequest(string? Email, string? Role);
    public sealed record AssignPipelinesRequest(List<string> PipelineIds);

    // ── List / Get ───────────────────────────────────────────────────────

    public async Task<List<UserDto>> ListUsersAsync(string? usernameFilter = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(usernameFilter))
            query = query.Where(u => u.Username.Contains(usernameFilter));

        var users = await query.OrderBy(u => u.Username).ToListAsync(ct);
        var assignments = await db.PipelineAssignments
            .GroupBy(pa => pa.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        return users.Select(u => new UserDto(
            u.UserId, u.Username, u.Email, u.Role.ToString(),
            u.IsActive, u.MustChangePassword, u.CreatedUtc,
            u.CreatedByUserId,
            assignments.GetValueOrDefault(u.UserId, 0)
        )).ToList();
    }

    public async Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return null;

        var count = await db.PipelineAssignments.CountAsync(pa => pa.UserId == userId, ct);
        return new UserDto(
            user.UserId, user.Username, user.Email, user.Role.ToString(),
            user.IsActive, user.MustChangePassword, user.CreatedUtc,
            user.CreatedByUserId, count);
    }

    // ── Create ───────────────────────────────────────────────────────────

    public async Task<(UserDto? User, string? GeneratedPassword, string? Error)> CreateUserAsync(
        CreateUserRequest request, string actorUserId, CancellationToken ct = default)
    {
        if (!Enum.TryParse<Role>(request.Role, true, out var role))
            return (null, null, "Invalid role");

        // Cannot create Administrator via this endpoint
        if (role == Role.Administrator)
            return (null, null, "Cannot create Administrator users via this endpoint");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Username uniqueness
        var exists = await db.Users.AnyAsync(u => u.Username == request.Username, ct);
        if (exists)
            return (null, null, "Username already exists");

        // Generate or use provided password
        var password = string.IsNullOrWhiteSpace(request.Password)
            ? GenerateRandomPassword()
            : request.Password;

        var user = new User
        {
            UserId = Guid.NewGuid().ToString("D").ToLowerInvariant(),
            Username = request.Username,
            Email = request.Email,
            Role = role,
            PasswordHash = _passwordHasher.Hash(password),
            MustChangePassword = true,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            CreatedByUserId = actorUserId,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = actorUserId,
            ActionName = "User_Create",
            ResourceId = user.UserId,
            Allowed = true,
            ReasonCode = "admin",
            ClientKind = ClientKind.Wpf,
        });

        var dto = new UserDto(user.UserId, user.Username, user.Email, user.Role.ToString(),
            user.IsActive, user.MustChangePassword, user.CreatedUtc, user.CreatedByUserId, 0);

        return (dto, password, null);
    }

    // ── Update ───────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> UpdateUserAsync(
        string userId, UpdateUserRequest request, string actorUserId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return (false, "User not found");

        if (request.Email is not null)
            user.Email = request.Email;

        if (request.Role is not null)
        {
            if (!Enum.TryParse<Role>(request.Role, true, out var newRole))
                return (false, "Invalid role");
            if (newRole == Role.Administrator)
                return (false, "Cannot assign Administrator role via this endpoint");

            user.Role = newRole;
            // Role change revokes all sessions (Design §4.3)
            await _sessionStore.RevokeAllForUserAsync(userId, ct);
        }

        await db.SaveChangesAsync(ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = actorUserId,
            ActionName = "User_Update",
            ResourceId = userId,
            Allowed = true,
            ReasonCode = "admin",
            ClientKind = ClientKind.Wpf,
        });

        return (true, null);
    }

    // ── Delete ───────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> DeleteUserAsync(
        string userId, string actorUserId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return (false, "User not found");

        // FR-USR-05: Cannot delete last Administrator
        if (user.Role == Role.Administrator)
        {
            var adminCount = await db.Users.CountAsync(
                u => u.Role == Role.Administrator && u.IsActive && u.UserId != userId, ct);
            if (adminCount == 0)
                return (false, "Cannot delete the last Administrator");
        }

        // FR-USR-04: Cascade delete assignments in a single transaction
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var assignments = await db.PipelineAssignments
            .Where(pa => pa.UserId == userId)
            .ToListAsync(ct);

        // Audit each revoked assignment
        foreach (var assignment in assignments)
        {
            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = actorUserId,
                ActionName = "User_Revoke",
                ResourceId = userId,
                Allowed = true,
                ReasonCode = "cascade-delete",
                ClientKind = ClientKind.Wpf,
            });
        }

        db.PipelineAssignments.RemoveRange(assignments);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Revoke all sessions for deleted user
        await _sessionStore.RevokeAllForUserAsync(userId, ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = actorUserId,
            ActionName = "User_Delete",
            ResourceId = userId,
            Allowed = true,
            ReasonCode = "admin",
            ClientKind = ClientKind.Wpf,
        });

        return (true, null);
    }

    // ── Pipeline Assignments ─────────────────────────────────────────────

    public async Task<List<string>> GetAssignmentsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.PipelineAssignments
            .Where(pa => pa.UserId == userId)
            .Select(pa => pa.PipelineId)
            .ToListAsync(ct);
    }

    public async Task<(bool Success, string? Error)> SetAssignmentsAsync(
        string userId, List<string> desiredPipelineIds, string actorUserId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return (false, "User not found");

        var current = await db.PipelineAssignments
            .Where(pa => pa.UserId == userId)
            .Select(pa => pa.PipelineId)
            .ToListAsync(ct);

        var currentSet = new HashSet<string>(current);
        var desiredSet = new HashSet<string>(desiredPipelineIds);

        var toAdd = desiredSet.Except(currentSet).ToList();
        var toRemove = currentSet.Except(desiredSet).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return (true, null);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Remove revoked assignments
        if (toRemove.Count > 0)
        {
            var removeEntities = await db.PipelineAssignments
                .Where(pa => pa.UserId == userId && toRemove.Contains(pa.PipelineId))
                .ToListAsync(ct);
            db.PipelineAssignments.RemoveRange(removeEntities);

            foreach (var pipelineId in toRemove)
            {
                _auditWriter.Enqueue(new AuditEntry
                {
                    UserId = actorUserId,
                    ActionName = "User_Revoke",
                    ResourceId = userId,
                    Allowed = true,
                    ReasonCode = "admin",
                    ClientKind = ClientKind.Wpf,
                });
            }
        }

        // Add new assignments
        foreach (var pipelineId in toAdd)
        {
            db.PipelineAssignments.Add(new PipelineAssignment
            {
                UserId = userId,
                PipelineId = pipelineId,
                AssignedUtc = DateTime.UtcNow,
                AssignedByUserId = actorUserId,
            });

            _auditWriter.Enqueue(new AuditEntry
            {
                UserId = actorUserId,
                ActionName = "User_Assign",
                ResourceId = userId,
                Allowed = true,
                ReasonCode = "admin",
                ClientKind = ClientKind.Wpf,
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return (true, null);
    }

    // ── Reset Password ───────────────────────────────────────────────────

    public async Task<(string? NewPassword, string? Error)> ResetPasswordAsync(
        string userId, string actorUserId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return (null, "User not found");

        var newPassword = GenerateRandomPassword();
        user.PasswordHash = _passwordHasher.Hash(newPassword);
        user.MustChangePassword = true;
        await db.SaveChangesAsync(ct);

        // Revoke all sessions so user must re-login
        await _sessionStore.RevokeAllForUserAsync(userId, ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = actorUserId,
            ActionName = "User_ResetPassword",
            ResourceId = userId,
            Allowed = true,
            ReasonCode = "admin",
            ClientKind = ClientKind.Wpf,
        });

        return (newPassword, null);
    }

    // ── Username check ───────────────────────────────────────────────────

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(u => u.Username == username, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string GenerateRandomPassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string special = "!@#$%&*";
        const string all = lower + upper + digits + special;

        var password = new char[16];
        // Ensure at least one of each required class
        password[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        password[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        password[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        password[3] = special[RandomNumberGenerator.GetInt32(special.Length)];

        for (int i = 4; i < password.Length; i++)
            password[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Shuffle
        RandomNumberGenerator.Shuffle(password.AsSpan());
        return new string(password);
    }
}
