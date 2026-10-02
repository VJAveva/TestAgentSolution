namespace TestControllerGrpc.Services;

/// <summary>
/// The single colour vocabulary for every generated report and e-mail, using Microsoft Fluent neutrals and
/// semantic colours so output matches Outlook rather than the dark in-app theme.
/// </summary>
/// <remarks>
/// Values are raw hex so they can be inlined into <c>style="..."</c> attributes: Outlook's Word renderer drops
/// stylesheets, CSS custom properties, flexbox and grid, so e-mail bodies must use tables plus inline styles.
/// Only the browser-viewed trend report may use variables and modern layout.
/// </remarks>
public static class EmailPalette
{
    public const string FontStack = "'Segoe UI', Arial, sans-serif";

    // Neutrals
    public const string PageBg = "#FAF9F8";
    public const string Surface = "#FFFFFF";
    public const string SurfaceAlt = "#F3F2F1";
    public const string Border = "#EDEBE9";
    public const string Divider = "#E1DFDD";

    // Text
    public const string Text = "#323130";
    public const string TextSecondary = "#605E5C";
    public const string TextTertiary = "#A19F9D";

    // Semantic
    public const string Accent = "#0078D4";
    public const string Success = "#107C10";
    public const string SuccessBg = "#DFF6DD";
    public const string Warning = "#CA5010";
    public const string WarningBg = "#FFF4CE";
    public const string Danger = "#D13438";
    public const string DangerBg = "#FDE7E9";

    /// <summary>ASCII separator for subject lines; non-ASCII dashes arrive mojibaked in some mail clients.</summary>
    public const string SubjectSeparator = " - ";
}
