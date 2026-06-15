using Microsoft.Extensions.Options;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Services;

/// <summary>
/// Holds the active IUserContext for the WPF session.
/// Singleton. Wired to AuthClient.AuthStateChanged.
/// </summary>
public sealed class CurrentUserHolder
{
    private readonly IOptionsMonitor<RbacOptions> _rbacOptions;
    private IUserContext _user;
    private bool? _isSecuredModeOverride;

    public CurrentUserHolder(IOptionsMonitor<RbacOptions> rbacOptions)
    {
        _rbacOptions = rbacOptions;
        _user = DefaultUser.ForClient(ClientKind.Wpf);
    }

    /// <summary>Current user context for UI capability checks.</summary>
    public IUserContext User => _user;

    /// <summary>True when RBAC is enabled (Secured mode).
    /// Uses authoritative override from SignalR ModeChanged when available,
    /// falls back to IOptionsMonitor for initial startup.</summary>
    public bool IsSecuredMode => _isSecuredModeOverride ?? _rbacOptions.CurrentValue.Enabled;

    /// <summary>Raised when the user changes (login, logout, refresh, mode switch).</summary>
    public event Action? UserChanged;

    /// <summary>Set the authoritative mode from a live ModeChanged event (bypasses stale IOptionsMonitor).</summary>
    public void SetMode(bool isSecured)
    {
        _isSecuredModeOverride = isSecured;
    }

    /// <summary>Set the active user from AuthClient login response.</summary>
    public void SetUser(AuthUserInfo info)
    {
        _user = new SyntheticUserContext(
            userId: info.UserId,
            displayName: info.DisplayName,
            clientKind: ClientKind.Wpf,
            roles: [info.Role],
            assignedPipelineIds: new HashSet<string>(info.AssignedPipelineIds ?? []));
        UserChanged?.Invoke();
    }

    /// <summary>Clear on logout — reverts to Default user.</summary>
    public void Clear()
    {
        _user = DefaultUser.ForClient(ClientKind.Wpf);
        UserChanged?.Invoke();
    }

    /// <summary>Set to Default user (used when RBAC:Enabled = false).</summary>
    public void SetDefaultUser()
    {
        _user = DefaultUser.ForClient(ClientKind.Wpf);
        UserChanged?.Invoke();
    }
}
