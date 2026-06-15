using Microsoft.Extensions.Options;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Services;

/// <summary>
/// Synchronous capability checker for WPF UI bindings.
/// UX hint only — NOT security. Server-side guard remains authoritative.
/// Singleton. Works entirely off cached IUserContext state (no network, no DB).
/// </summary>
public sealed class CapabilityChecker
{
    private readonly CurrentUserHolder _userHolder;

    public CapabilityChecker(CurrentUserHolder userHolder, IOptionsMonitor<RbacOptions> rbacOptions)
    {
        _userHolder = userHolder;
        _userHolder.UserChanged += OnUserChanged;
    }

    /// <summary>
    /// Raised when capabilities may have changed (user login/logout/refresh).
    /// May fire from a background thread — callers must marshal to UI thread.
    /// </summary>
    public event Action? CapabilitiesChanged;

    /// <summary>
    /// Synchronous permission check for UI bindings.
    /// Returns true if the current user can perform the given permission on the resource.
    /// </summary>
    public bool Can(Permission permission, string? resourceId = null)
    {
        // Default mode: WPF gets full access
        if (!_userHolder.IsSecuredMode)
            return true;

        var user = _userHolder.User;
        var role = user.Roles.FirstOrDefault();

        // Admin shortcut: all permissions
        if (string.Equals(role, Role.Administrator.ToString(), StringComparison.OrdinalIgnoreCase))
            return true;

        // Check role-based permission catalog
        var permissions = PermissionCatalog.GetPermissionsForRole(role);
        if (!permissions.Contains(permission))
            return false;

        // Resource-scoped check for Engineers (assignment-based)
        if (resourceId is not null
            && string.Equals(role, Role.Engineer.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return user.AssignedPipelineIds.Contains(resourceId);
        }

        return true;
    }

    private void OnUserChanged()
    {
        CapabilitiesChanged?.Invoke();
    }
}
