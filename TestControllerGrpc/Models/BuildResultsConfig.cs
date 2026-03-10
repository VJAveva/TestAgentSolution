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
}
