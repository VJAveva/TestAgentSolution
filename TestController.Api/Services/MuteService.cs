using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Manages notification mutes and cooldown state. Singleton, uses IDbContextFactory.
/// Per Phase 8 — mute/unmute gated by Notification_Mute permission.
/// </summary>
public sealed class MuteService
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly IAuthorizationService _authorizationService;
    private readonly IAuditWriter _auditWriter;
    private readonly IAppLogger _logger;

    public MuteService(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        IAuthorizationService authorizationService,
        IAuditWriter auditWriter,
        IAppLogger logger)
    {
        _dbFactory = dbFactory;
        _authorizationService = authorizationService;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public async Task<List<NotificationMute>> ListMutesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.NotificationMutes
            .OrderByDescending(m => m.MutedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<(bool Success, string? Error)> MuteAsync(
        IUserContext user, string target, string targetType, CancellationToken ct = default)
    {
        var decision = await _authorizationService.CanAsync(user, Permission.Notification_Mute, target);
        if (!decision.Allowed)
            return (false, decision.HumanReadable ?? "Not authorized");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.NotificationMutes
            .FirstOrDefaultAsync(m => m.Target == target && m.TargetType == targetType, ct);
        if (existing is not null)
            return (true, null); // already muted

        db.NotificationMutes.Add(new NotificationMute
        {
            Target = target,
            TargetType = targetType,
            MutedByUserId = user.UserId ?? user.GuestId ?? "unknown",
            MutedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.UserId,
            GuestId = user.GuestId,
            RoleAtTime = Enum.TryParse<Role>(user.Roles.FirstOrDefault(), out var r) ? r : null,
            ActionName = "Notification_Mute",
            ResourceId = target,
            Allowed = true,
            ReasonCode = "muted",
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
        });

        _logger.Info("Notifications", $"Muted {targetType} '{target}' by {user.UserId ?? user.GuestId}");
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UnmuteAsync(
        IUserContext user, long muteId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mute = await db.NotificationMutes.FindAsync([muteId], ct);
        if (mute is null)
            return (false, "Mute not found");

        var decision = await _authorizationService.CanAsync(user, Permission.Notification_Mute, mute.Target);
        if (!decision.Allowed)
            return (false, decision.HumanReadable ?? "Not authorized");

        db.NotificationMutes.Remove(mute);
        await db.SaveChangesAsync(ct);

        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.UserId,
            GuestId = user.GuestId,
            RoleAtTime = Enum.TryParse<Role>(user.Roles.FirstOrDefault(), out var r) ? r : null,
            ActionName = "Notification_Unmute",
            ResourceId = mute.Target,
            Allowed = true,
            ReasonCode = "unmuted",
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
        });

        _logger.Info("Notifications", $"Unmuted {mute.TargetType} '{mute.Target}' by {user.UserId ?? user.GuestId}");
        return (true, null);
    }

    /// <summary>Checks if a target is currently muted (used by dispatcher).</summary>
    public async Task<bool> IsMutedAsync(string target, string targetType, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.NotificationMutes.AnyAsync(
            m => m.Target == target && m.TargetType == targetType
                 && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > DateTime.UtcNow), ct);
    }

    /// <summary>Checks if target is within cooldown window. Returns true if should suppress.</summary>
    public async Task<bool> IsInCooldownAsync(string target, string targetType, int cooldownHours, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.AddHours(-cooldownHours);
        return await db.NotificationCooldowns.AnyAsync(
            c => c.Target == target && c.TargetType == targetType && c.LastSentUtc > cutoff, ct);
    }

    /// <summary>Records that a notification was sent (updates cooldown).</summary>
    public async Task RecordSentAsync(string target, string targetType, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.NotificationCooldowns
            .FirstOrDefaultAsync(c => c.Target == target && c.TargetType == targetType, ct);
        if (existing is not null)
        {
            existing.LastSentUtc = DateTime.UtcNow;
        }
        else
        {
            db.NotificationCooldowns.Add(new NotificationCooldown
            {
                Target = target,
                TargetType = targetType,
                LastSentUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
