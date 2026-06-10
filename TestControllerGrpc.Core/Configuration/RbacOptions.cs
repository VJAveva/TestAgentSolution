namespace TestControllerGrpc.Configuration;

/// <summary>
/// Options bound to the "RBAC" section of appsettings.json.
/// Single boolean controls Default vs Secured mode.
/// Per 05_Default_Mode_Design.md §2.1.
/// </summary>
public sealed class RbacOptions
{
    public const string SectionName = "RBAC";

    /// <summary>
    /// false = Default mode (no authentication required, WPF full-access, Web read-only).
    /// true = Secured mode (full RBAC enforcement).
    /// </summary>
    public bool Enabled { get; set; }
}
