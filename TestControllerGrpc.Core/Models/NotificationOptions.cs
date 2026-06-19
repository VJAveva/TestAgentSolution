namespace TestControllerGrpc.Models;

/// <summary>
/// Configuration for automatic notification dispatch. Bound from "Notifications" config section.
/// Reuses SMTP and recipient settings from BuildResultsConfig.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Master toggle for automatic alert emails on threshold crossings.</summary>
    public bool AutoAlertEnabled { get; set; } = true;

    /// <summary>When true, only fire on threshold crossings (consecutive fail >= N, new flaky).
    /// When false, fires on every run with failures (not recommended — duplicates CI email).</summary>
    public bool AlertOnThresholdOnly { get; set; } = true;

    /// <summary>Hours to suppress duplicate alerts for the same target.</summary>
    public int CooldownHours { get; set; } = 6;
}
