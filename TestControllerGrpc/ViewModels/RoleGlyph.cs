namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Shared role → glyph/label mapping for owner attribution badges on the
/// Execution Dashboard and Agent Fleet. Mirrors the mapping in
/// <see cref="TestControllerGrpc.Controls.UserIdentityBadge"/> so the icons
/// stay consistent wherever a triggering user's role is surfaced.
/// </summary>
public static class RoleGlyph
{
    /// <summary>Emoji glyph for a role, or the person glyph when unknown/default.</summary>
    public static string Icon(string? role) => role switch
    {
        "Administrator" => "🛡",
        "SeniorManager" => "👔",
        "Engineer" => "🔧",
        "Guest" => "🎫",
        "Observer" => "👁",
        _ => "👤",
    };

    /// <summary>Short, UI-friendly label for a role.</summary>
    public static string Label(string? role) => role switch
    {
        "Administrator" => "Admin",
        "SeniorManager" => "Sr. Mgr",
        "Engineer" => "Engineer",
        "Guest" => "Guest",
        "Observer" => "Observer",
        _ => string.IsNullOrWhiteSpace(role) ? "" : role,
    };
}
