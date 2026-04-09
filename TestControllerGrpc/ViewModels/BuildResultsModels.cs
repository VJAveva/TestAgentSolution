using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>Scope mode: single build vs consolidated multi-build reporting.</summary>
public enum ReportScope { SingleBuild, Consolidated }

/// <summary>Predefined time range filters for consolidated reporting.</summary>
public enum TimeRangeFilter { OneDay, OneWeek, OneMonth, Custom }

/// <summary>Item shown in the build dropdown list.</summary>
public partial class BuildListItem : ObservableObject
{
    [ObservableProperty] private string _buildNumber = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private DateTime _modifiedDate;
    [ObservableProperty] private bool _hasBeenLoaded;

    public override string ToString() => $"{BuildNumber}  ({ModifiedDate:yyyy-MM-dd HH:mm})";
}

/// <summary>Unified tree node for Build ? UseCase ? TestResult hierarchy (used by legacy TreeView).</summary>
public partial class ResultsTreeNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int? _total;
    [ObservableProperty] private int? _passed;
    [ObservableProperty] private int? _failed;
    [ObservableProperty] private int? _notExecuted;
    [ObservableProperty] private double? _passRate;
    [ObservableProperty] private string _passRateColor = "#10B981";
    [ObservableProperty] private string _nodeLevel = "";  // "Build", "UseCase", "TestResult"
    [ObservableProperty] private DateTime? _modifiedDate;
    [ObservableProperty] private bool _isExpanded;

    // TestResult-level
    [ObservableProperty] private string? _outcome;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _fullError = "";
    [ObservableProperty] private string _fullStackTrace = "";

    // Export (build-level only)
    [ObservableProperty] private bool _showExportButtons;
    public IRelayCommand? ExportHtmlCommand { get; set; }
    public IRelayCommand? ExportCsvCommand { get; set; }

    public ObservableCollection<ResultsTreeNode> Children { get; } = new();

    public string PassRateFormatted => PassRate.HasValue ? $"{PassRate.Value:F1}%" : "";
    public bool HasChildren => Children.Count > 0;
}

/// <summary>Flat node for the ListView-based grid with indent support and detail panel.</summary>
public partial class ResultsFlatNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _nodeLevel = "";  // "AllBuilds", "Build", "UseCase", "TestResult"
    [ObservableProperty] private string? _buildNumber;
    [ObservableProperty] private int? _total;
    [ObservableProperty] private int? _passed;
    [ObservableProperty] private int? _failed;
    [ObservableProperty] private int? _notExecuted;
    [ObservableProperty] private double? _passRate;
    [ObservableProperty] private string _passRateColor = "#10B981";
    [ObservableProperty] private int _indentLevel;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _showEmailButton;
    [ObservableProperty] private bool _showStats = true;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandIcon));
    }

    // TestResult-level
    [ObservableProperty] private string? _outcome;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _fullError = "";
    [ObservableProperty] private string _fullStackTrace = "";
    public TestResult? TestResultModel { get; set; }

    // Export (build-level)
    public bool ShowExportButtons => NodeLevel is "Build" or "AllBuilds";
    public IRelayCommand? ExportHtmlCommand { get; set; }
    public IRelayCommand? ExportCsvCommand { get; set; }

    // Commands
    public IRelayCommand? SendEmailCommand { get; set; }
    public IRelayCommand? ToggleExpandCommand { get; set; }

    /// <summary>Left margin based on indent level (for tree-like display in a flat list).</summary>
    public Thickness IndentMargin => new(IndentLevel * 20, 0, 0, 0);

    public string PassRateFormatted => PassRate.HasValue ? $"{PassRate.Value:F1}%" : "";

    /// <summary>Expand/collapse icon glyph.</summary>
    public string ExpandIcon => IsExpanded ? "\u25BC" : "\u25B6";

    /// <summary>Whether this node has expandable children.</summary>
    public bool CanExpand => NodeLevel is "AllBuilds" or "Build" or "UseCase";

    /// <summary>Outcome icon for test results.</summary>
    public string OutcomeIcon => Outcome switch
    {
        "Passed" => "\u2713",
        "Failed" => "\u2717",
        "Timeout" => "\u23F1",
        _ => "\u25CB",
    };
}

/// <summary>Simple VM for displaying execution step rows in the detail panel.</summary>
public class StepRowVM
{
    public string StepName { get; set; } = "";
    public string Outcome { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string OutcomeIcon { get; set; } = "\u25CB";
    public string DurationText { get; set; } = "";
}

/// <summary>ViewModel for per-UseCase trend data across builds.</summary>
public class UseCaseTrendViewModel
{
    public string UseCaseName { get; set; } = "";
    public List<UseCaseTrendEntry> Entries { get; set; } = new();
    public double LatestPassRate { get; set; }
    public string Trend { get; set; } = "Stable";
    public string TrendIcon => Trend switch
    {
        "Improving" => "\u25B2",
        "Declining" => "\u25BC",
        _ => "\u2014"
    };
    public string TrendColor => Trend switch
    {
        "Improving" => "#10B981",
        "Declining" => "#EF4444",
        _ => "#94A3B8"
    };
}
