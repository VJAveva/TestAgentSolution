using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Authorization;

/// <summary>
/// Static role-to-permission mapping for capability lists returned by /api/auth/me.
/// Per 01_System_Design.md §3.1 permission matrix.
/// </summary>
public static class PermissionCatalog
{
    private static readonly Permission[] AdminPermissions = Enum.GetValues<Permission>();

    private static readonly Permission[] SeniorManagerPermissions =
    [
        Permission.Pipeline_View,
        Permission.Pipeline_Trigger,
        Permission.Pipeline_Cancel,
        Permission.Pipeline_Retry,
        Permission.Pipeline_TriggerAll,
        Permission.Pipeline_CancelAll,
        Permission.Pipeline_ForceRelease,
        Permission.Report_View,
        Permission.Report_Generate,
        Permission.Notification_Mute,
        // Must mirror AuthorizationService.IsSeniorManagerOnlyPermission; PermissionCatalogConsistencyTests pins it.
        Permission.CodeChurn_View,
        Permission.CodeChurn_Export,
        Permission.ReportCard_View,
    ];

    private static readonly Permission[] EngineerPermissions =
    [
        Permission.Pipeline_View,
        Permission.Pipeline_Trigger,
        Permission.Pipeline_Cancel,
        Permission.Pipeline_Retry,
        Permission.Report_View,
        Permission.Notification_Mute,
    ];

    private static readonly Permission[] GuestPermissions =
    [
        Permission.Pipeline_View,
        Permission.Report_View,
    ];

    public static IReadOnlyList<Permission> GetPermissionsForRole(string? roleName)
    {
        if (roleName is null) return GuestPermissions;

        if (Enum.TryParse<Role>(roleName, ignoreCase: true, out var role))
        {
            return role switch
            {
                Role.Administrator => AdminPermissions,
                Role.SeniorManager => SeniorManagerPermissions,
                Role.Engineer => EngineerPermissions,
                Role.Guest => GuestPermissions,
                _ => GuestPermissions,
            };
        }

        return GuestPermissions;
    }
}
