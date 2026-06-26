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

    // ── Fleet alerting (P2-4) ─────────────────────────────────────────────

    /// <summary>Send an email when an agent transitions to unhealthy (circuit open / connection lost).</summary>
    public bool AgentDownAlertEnabled { get; set; } = true;

    /// <summary>Minutes to wait before re-alerting on an agent that stays down. Prevents per-tick spam.</summary>
    public int AgentDownReAlertMinutes { get; set; } = 30;

    /// <summary>Send an email when an active run exceeds <see cref="RunOverrunMinutes"/>.</summary>
    public bool RunOverrunAlertEnabled { get; set; } = true;

    /// <summary>A running session beyond this many minutes is flagged as overrunning (alerts once per session).</summary>
    public int RunOverrunMinutes { get; set; } = 120;

    /// <summary>How often the fleet alert dispatcher polls agent health and active sessions.</summary>
    public int FleetCheckIntervalSeconds { get; set; } = 30;
}
