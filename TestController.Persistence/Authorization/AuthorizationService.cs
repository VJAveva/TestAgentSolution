using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Persistence.Authorization;

/// <summary>
/// Authorization service implementation per 01_System_Design.md §3.2 + §3.4.
/// Default-mode short-circuit comes BEFORE role evaluation.
/// Singleton; accesses DbContext via IDbContextFactory.
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly IOptionsMonitor<RbacOptions> _options;
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly IAuditWriter _auditWriter;

    public AuthorizationService(
        IOptionsMonitor<RbacOptions> options,
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        IAuditWriter auditWriter)
    {
        _options = options;
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
    }

    public async Task<AuthDecision> CanAsync(
        IUserContext user,
        Permission permission,
        string? resourceId = null,
        CancellationToken ct = default)
    {
        // Default-mode short-circuit per 05_Default_Mode_Design.md §4
        if (!_options.CurrentValue.Enabled)
        {
            var defaultDecision = EvaluateDefaultMode(user, permission);
            EnqueueAudit(user, permission, resourceId, defaultDecision);
            return defaultDecision;
        }

        // Normal RBAC evaluation
        var decision = await EvaluateRbacAsync(user, permission, resourceId, ct);
        EnqueueAudit(user, permission, resourceId, decision);
        return decision;
    }

    private static AuthDecision EvaluateDefaultMode(IUserContext user, Permission permission)
    {
        if (user.ClientKind == ClientKind.Wpf)
            return AuthDecision.Allow("default-mode-wpf");

        if (user.ClientKind == ClientKind.Web && IsReadPermission(permission))
            return AuthDecision.Allow("default-mode-web-read");

        return AuthDecision.Deny("default-mode-web-readonly", "Web client is read-only in Default mode");
    }

    private async Task<AuthDecision> EvaluateRbacAsync(
        IUserContext user, Permission permission, string? resourceId, CancellationToken ct)
    {
        var role = ResolveRole(user);

        // Admin shortcut — unconditional allow
        if (role == Role.Administrator)
            return AuthDecision.Allow("admin");

        // Guest restriction
        if (role == Role.Guest)
        {
            return IsReadPermission(permission)
                ? AuthDecision.Allow("guest-read")
                : AuthDecision.Deny("guest-readonly", "Guest users have read-only access");
        }

        // SeniorManager — allowed on pipeline-scoped + reports, but NOT User_*, Pipeline_Enable/Disable, Audit_*
        if (role == Role.SeniorManager)
        {
            if (IsAdminOnlyPermission(permission))
                return AuthDecision.Deny("no-role", $"Senior Managers cannot perform {permission}");
            return AuthDecision.Allow("sr-mgr");
        }

        // Engineer — requires pipeline assignment for pipeline-scoped permissions
        if (role == Role.Engineer)
        {
            if (IsAdminOnlyPermission(permission))
                return AuthDecision.Deny("no-role", $"Engineers cannot perform {permission}");

            if (IsSeniorManagerOnlyPermission(permission))
                return AuthDecision.Deny("no-role", $"Engineers cannot perform {permission}");

            if (IsPipelineScopedPermission(permission) && resourceId is not null)
            {
                // Check assignment from pre-fetched set first (fast path)
                if (user.AssignedPipelineIds.Contains(resourceId))
                    return AuthDecision.Allow("engineer-assigned");

                // Fallback to DB check if pre-fetch was empty (edge case: stale context)
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var assigned = await db.PipelineAssignments
                    .AnyAsync(pa => pa.UserId == user.UserId && pa.PipelineId == resourceId, ct);

                return assigned
                    ? AuthDecision.Allow("engineer-assigned")
                    : AuthDecision.Deny("no-assignment", $"Not assigned to pipeline {resourceId}");
            }

            // Non-pipeline-scoped read permissions
            if (IsReadPermission(permission))
                return AuthDecision.Allow("engineer-read");

            // Notification_Mute for own pipelines
            if (permission == Permission.Notification_Mute && resourceId is not null)
            {
                if (user.AssignedPipelineIds.Contains(resourceId))
                    return AuthDecision.Allow("engineer-own-mute");
                return AuthDecision.Deny("no-assignment", "Can only mute notifications for assigned pipelines");
            }

            return AuthDecision.Deny("no-role", $"Engineers cannot perform {permission}");
        }

        return AuthDecision.Deny("not-handled");
    }

    private static Role ResolveRole(IUserContext user)
    {
        if (user.GuestId is not null) return Role.Guest;
        if (user.Roles.Count == 0) return Role.Guest;

        // Take highest-privilege role
        foreach (var r in user.Roles)
        {
            if (Enum.TryParse<Role>(r, true, out var parsed))
                return parsed;
        }
        return Role.Guest;
    }

    private static bool IsReadPermission(Permission p) => p is
        Permission.Pipeline_View or
        Permission.Report_View;

    private static bool IsAdminOnlyPermission(Permission p) => p is
        Permission.User_Create or
        Permission.User_Update or
        Permission.User_Delete or
        Permission.User_Assign or
        Permission.User_Revoke or
        Permission.Pipeline_Enable or
        Permission.Pipeline_Disable or
        Permission.Audit_View or
        Permission.Audit_Export or
        Permission.System_ChangeMode;

    private static bool IsSeniorManagerOnlyPermission(Permission p) => p is
        Permission.Pipeline_TriggerAll or
        Permission.Pipeline_CancelAll or
        Permission.Pipeline_ForceRelease or
        Permission.Report_Generate;

    private static bool IsPipelineScopedPermission(Permission p) => p is
        Permission.Pipeline_Trigger or
        Permission.Pipeline_Cancel or
        Permission.Pipeline_Retry;

    private void EnqueueAudit(IUserContext user, Permission permission, string? resourceId, AuthDecision decision)
    {
        _auditWriter.Enqueue(new AuditEntry
        {
            UserId = user.GuestId is null ? user.UserId : null,
            GuestId = user.GuestId,
            RoleAtTime = user.Roles.Count > 0 && Enum.TryParse<Role>(user.Roles[0], true, out var r) ? r : null,
            ActionName = permission.ToString(),
            ResourceId = resourceId,
            Allowed = decision.Allowed,
            ReasonCode = decision.ReasonCode,
            TimestampUtc = DateTime.UtcNow,
            ClientKind = user.ClientKind,
        });
    }
}
