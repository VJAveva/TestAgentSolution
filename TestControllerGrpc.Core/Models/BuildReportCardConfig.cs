namespace TestControllerGrpc.Models;

/// <summary>
/// Configuration for the Build Report Card feature, bound from the
/// "BuildReportCard" section of appsettings.json.
/// </summary>
public sealed class BuildReportCardConfig
{
    /// <summary>Root folder containing one subfolder per CI (plus a PSR folder).</summary>
    public string ReportResultsRoot { get; set; } = @"C:\ReportResults";

    /// <summary>
    /// Top-level CI folders to aggregate. Each is consolidated (all nested
    /// subfolders merged) into a single CI result.
    /// </summary>
    public List<string> CiFolders { get; set; } =
    [
        "SmokeTestResults",
        "LegacyCI",
        "ChangePropagation",
        "WizardPropagation",
        "ObjectWizard",
        "SymbolWizard",
    ];

    /// <summary>Folder (under the root) holding PSR result files (Excel — copied later).</summary>
    public string PsrFolderName { get; set; } = "PSR";

    /// <summary>When true (or when no PSR files exist), show placeholder PSR cards.</summary>
    public bool UseDummyPsr { get; set; } = true;

    /// <summary>CI name → owning team/email mapping for the failures table.</summary>
    public Dictionary<string, CiOwner> Owners { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configurable grade penalty weights and letter thresholds.</summary>
    public GradeWeights Grade { get; set; } = new();

    // CI colour thresholds (pass-rate %).
    public double GreenThreshold { get; set; } = 98.0;
    public double BlueThreshold { get; set; } = 95.0;
    public double AmberThreshold { get; set; } = 90.0;

    /// <summary>Number of recent builds shown in the trend strip.</summary>
    public int TrendWindow { get; set; } = 10;

    /// <summary>
    /// Optional override for the build-summary JSON store used to compute
    /// trend / vs-last delta. Defaults to {ReportResultsRoot}\.reportcard\summaries.json.
    /// </summary>
    public string SummaryStorePath { get; set; } = "";
}

/// <summary>A CI's owning team and notification email.</summary>
public sealed class CiOwner
{
    public string Team { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>Configurable grade-algorithm penalty weights and A–F cut points.</summary>
public sealed class GradeWeights
{
    public double RegressionPenalty { get; set; } = 2.0;
    public double PsrFailurePenalty { get; set; } = 3.0;
    public double PsrWarningPenalty { get; set; } = 0.5;
    public double CiBelow90Penalty { get; set; } = 1.0;

    public double AThreshold { get; set; } = 97.0;
    public double BThreshold { get; set; } = 93.0;
    public double CThreshold { get; set; } = 88.0;
    public double DThreshold { get; set; } = 80.0;
}
