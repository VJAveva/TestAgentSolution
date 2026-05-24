using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestController.Api.Security;

/// <summary>
/// Verifies that the current user is the owner of a session or has Admin privileges.
/// </summary>
public interface ISessionOwnershipChecker
{
    /// <summary>
    /// Returns true if the user can operate on the given session (is owner OR admin).
    /// </summary>
    bool CanAccessSession(ClaimsPrincipal user, ExecutionSession session);

    /// <summary>
    /// Gets the security identifier (SID or token ID) from the current user principal.
    /// </summary>
    string GetUserSid(ClaimsPrincipal user);
}

public sealed class SessionOwnershipChecker : ISessionOwnershipChecker
{
    private readonly IAuthenticationModeProvider _modeProvider;
    private readonly ISecurityAuditLogger _auditLogger;

    public SessionOwnershipChecker(IAuthenticationModeProvider modeProvider, ISecurityAuditLogger auditLogger)
    {
        _modeProvider = modeProvider;
        _auditLogger = auditLogger;
    }

    public bool CanAccessSession(ClaimsPrincipal user, ExecutionSession session)
    {
        var role = _modeProvider.ResolveRole(user);
        if (role == UserRole.Admin)
            return true;

        var userSid = GetUserSid(user);
        if (string.IsNullOrEmpty(userSid))
            return false;

        // Check against OwnerSid first (new field), fall back to UserId for backwards compat
        if (!string.IsNullOrEmpty(session.OwnerSid))
            return string.Equals(session.OwnerSid, userSid, StringComparison.OrdinalIgnoreCase);

        // For pre-existing sessions that only have UserId (username string),
        // compare against both SID and display name to avoid false denials
        var userName = user.Identity?.Name ?? "";
        return string.Equals(session.UserId, userSid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(session.UserId, userName, StringComparison.OrdinalIgnoreCase);
    }

    public string GetUserSid(ClaimsPrincipal user)
    {
#pragma warning disable CA1416 // Platform compatibility — guarded by type check
        if (user.Identity is WindowsIdentity windowsIdentity)
            return windowsIdentity.User?.Value ?? windowsIdentity.Name ?? "";
#pragma warning restore CA1416

        // Token mode: use NameIdentifier claim
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity?.Name
            ?? "";
    }
}
