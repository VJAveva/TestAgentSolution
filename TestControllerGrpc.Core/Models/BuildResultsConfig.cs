namespace TestControllerGrpc.Models;

public class BuildResultsConfig
{
    public string ResultsRootPath { get; set; } = @"C:\TestResults";
    public double GoodThreshold { get; set; } = 95.0;
    public double WarningThreshold { get; set; } = 85.0;
    public string ReportRecipients { get; set; } = "";
    public string SmtpServer { get; set; } = "smtp";
    public int SmtpPort { get; set; } = 25;
    public string FromAddress { get; set; } = "";

    // Consecutive failure detection
    public string QaAlertRecipients { get; set; } = "";
    public int ConsecutiveFailThreshold { get; set; } = 2;
    public bool AlertOnLoad { get; set; } = true;

    // Consolidated run email, sent once when a full pipeline finishes. Off by default: turning it on starts
    // mailing every completed pipeline.
    public bool SendConsolidatedEmail { get; set; }

    /// <summary>Recipients for the consolidated email; falls back to <see cref="ReportRecipients"/> when blank.</summary>
    public string ConsolidatedRecipients { get; set; } = "";

    /// <summary>"Open in TestController" link. Blank hides the button.</summary>
    public string ConsolidatedControllerUrl { get; set; } = "";

    /// <summary>Report-card link; "{build}" is substituted with the run's build number. Blank hides the button.</summary>
    public string ConsolidatedReportCardUrl { get; set; } = "";
}
