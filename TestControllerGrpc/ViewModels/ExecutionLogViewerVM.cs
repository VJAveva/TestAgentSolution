using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// View-model backing the <c>ExecutionLogViewerDialog</c>. Holds the merged
/// timeline returned by <c>GET /api/results/builds/{b}/test/{t}/log</c> and
/// derives a filtered view based on source/severity/search toggles.
/// </summary>
public sealed partial class ExecutionLogViewerVM : ObservableObject
{
    public ExecutionLogViewerVM(LogPayload payload)
    {
        BuildName = payload.BuildName ?? "";
        TestCaseName = payload.TestCaseName ?? "";
        Outcome = payload.Outcome ?? "";
        Agent = payload.Agent ?? "";
        StartTime = payload.StartTime ?? "";
        EndTime = payload.EndTime ?? "";
        Duration = payload.Duration ?? "";
        FailedStepIndex = payload.FailedStepIndex;
        ErrorMessage = payload.ErrorMessage ?? "";
        StackTrace = payload.StackTrace ?? "";

        AllTimeline = (payload.MergedTimeline ?? new List<MergedLineDto>())
            .Select(l => new TimelineRow(l)).ToList();

        FilteredTimeline = new ObservableCollection<TimelineRow>(AllTimeline);
    }

    public string BuildName { get; }
    public string TestCaseName { get; }
    public string Outcome { get; }
    public string Agent { get; }
    public string StartTime { get; }
    public string EndTime { get; }
    public string Duration { get; }
    public int FailedStepIndex { get; }
    public string ErrorMessage { get; }
    public string StackTrace { get; }

    public string FailedStepDisplay =>
        FailedStepIndex >= 0 ? $"Step #{FailedStepIndex}" : "—";

    public string TimelineCountText =>
        $"{FilteredTimeline.Count} of {AllTimeline.Count} lines";

    public Brush OutcomeBrush => Outcome switch
    {
        "Passed" => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
        "Failed" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
    };

    public Visibility ErrorBlockVisibility =>
        string.IsNullOrEmpty(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<TimelineRow> AllTimeline { get; }
    public ObservableCollection<TimelineRow> FilteredTimeline { get; }

    [ObservableProperty] private bool _showAll = true;
    [ObservableProperty] private bool _showTrx = true;
    [ObservableProperty] private bool _showAgent = true;
    [ObservableProperty] private bool _showController = true;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _searchText = "";

    partial void OnShowAllChanged(bool value)
    {
        if (value) { ShowTrx = ShowAgent = ShowController = true; }
        ApplyFilter();
    }
    partial void OnShowTrxChanged(bool value) => ApplyFilter();
    partial void OnShowAgentChanged(bool value) => ApplyFilter();
    partial void OnShowControllerChanged(bool value) => ApplyFilter();
    partial void OnErrorsOnlyChanged(bool value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredTimeline.Clear();
        var search = (SearchText ?? "").Trim();
        foreach (var row in AllTimeline)
        {
            if (!IncludeSource(row.Source)) continue;
            if (ErrorsOnly && row.Severity != "Error") continue;
            if (!string.IsNullOrEmpty(search) &&
                !row.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredTimeline.Add(row);
        }
        OnPropertyChanged(nameof(TimelineCountText));
    }

    private bool IncludeSource(string source) => source switch
    {
        "TRX" => ShowTrx,
        "Agent" => ShowAgent,
        "Controller" => ShowController,
        _ => true,
    };

    // ?? DTOs (match the JSON returned by the API) ????????????????????

    public sealed class LogPayload
    {
        public string? BuildName { get; set; }
        public string? TestCaseName { get; set; }
        public string? Outcome { get; set; }
        public string? Agent { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? Duration { get; set; }
        public int FailedStepIndex { get; set; } = -1;
        public string? ErrorMessage { get; set; }
        public string? StackTrace { get; set; }
        public List<MergedLineDto>? MergedTimeline { get; set; }
    }

    public sealed class MergedLineDto
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class TimelineRow
    {
        public TimelineRow(MergedLineDto dto)
        {
            Timestamp = dto.Timestamp;
            Source = dto.Source;
            Severity = dto.Severity;
            Message = dto.Message;
            TimeText = dto.Timestamp.ToString("HH:mm:ss.fff");

            SourceBrush = dto.Source switch
            {
                "TRX" => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                "Agent" => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
                "Controller" => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
                _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            };

            SeverityBrush = dto.Severity switch
            {
                "Error" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                "Warning" => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            };
        }

        public DateTime Timestamp { get; }
        public string Source { get; }
        public string Severity { get; }
        public string Message { get; }
        public string TimeText { get; }
        public Brush SourceBrush { get; }
        public Brush SeverityBrush { get; }
    }
}
