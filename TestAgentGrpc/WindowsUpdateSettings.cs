namespace TestAgentGrpc;

/// <summary>
/// Windows Update detection knobs, bound from appsettings.json → "WindowsUpdate". Poll interval, coalescing window
/// and the enable flag are the values the controller may push down at runtime (spec §24/R24).
/// </summary>
public sealed class WindowsUpdateSettings
{
    public const string SectionName = "WindowsUpdate";

    /// <summary>Master switch; when false the detector does not start.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Registry poll interval. Spec default: 5 minutes.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Quiet period before a burst of detections is flushed as one event.</summary>
    public int CoalescingWindowSeconds { get; set; } = 30;

    /// <summary>Hard cap on how long coalescing may defer a flush.</summary>
    public int MaxCoalescingSeconds { get; set; } = 300;

    /// <summary>Unconditional full-state resend, so a controller that missed events recovers.</summary>
    public int SnapshotIntervalSeconds { get; set; } = 3600;

    /// <summary>Grace period before the first scan, so startup work is not competing with it.</summary>
    public int StartupDelaySeconds { get; set; } = 20;

    /// <summary>Hard timeout for the blocking WUApi search.</summary>
    public int ScanTimeoutSeconds { get; set; } = 180;

    /// <summary>When false, skips the WUApi pending-update search and reports reboot-required only.</summary>
    public bool ScanPendingUpdates { get; set; } = true;

    /// <summary>When false, skips the Windows Update event-log subscription.</summary>
    public bool WatchEventLog { get; set; } = true;

    /// <summary>Cap on update titles reported per event.</summary>
    public int MaxReportedItems { get; set; } = 30;

    /// <summary>
    /// Whether a pending file rename counts as "reboot required". Off, because ANY installer replacing a
    /// locked file sets it — measured 2026-09-22 on this fleet, every flagged node was tripping on AVEVA's
    /// own AMP.Installer and MSI rollback files, with no Windows Update pending at all. A reboot-required
    /// node stops taking pipeline work, so this was blocking runs on a signal that had nothing to do with
    /// Windows Update. Still reported via <c>PendingFileRenameCount</c>.
    /// </summary>
    public bool TreatPendingFileRenamesAsRebootRequired { get; set; }

    /// <summary>Windows' own last successful search older than this marks the posture Stale.</summary>
    public int StaleAfterDays { get; set; } = 7;
}
