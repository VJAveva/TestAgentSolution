using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// View-model wrapping a <see cref="FailureAnalysisReport"/> for the
/// <c>FailureAnalysisDialog</c>. Provides display strings, brushes and
/// derived collections for the timeline and signature grid.
/// </summary>
public sealed partial class FailureAnalysisVM : ObservableObject
{
    public FailureAnalysisReport Report { get; }

    public FailureAnalysisVM(FailureAnalysisReport report)
    {
        Report = report;
        History = report.History.Select(h => new BuildHistoryCell(h)).ToList();
        FailureSignatures = report.FailureSignatures
            .Select(s => new SignatureRow(s)).ToList();
    }

    public string TestCaseName => Report.TestCaseName;
    public string Verdict => Report.Verdict;
    public string SuggestedAction => Report.SuggestedAction;
    public string PatternLabel => Report.Pattern.ToString();
    public string ConfidenceText => $"{Report.Confidence}%";
    public string? LatestBuildName => Report.History.FirstOrDefault()?.BuildName;
    public int LatestFailedStepIndex =>
        Report.FailureSignatures.FirstOrDefault()?.FailedStepIndex ?? -1;

    public IReadOnlyList<BuildHistoryCell> History { get; }
    public IReadOnlyList<SignatureRow> FailureSignatures { get; }

    public Brush VerdictBorderBrush => Report.Pattern switch
    {
        FailurePattern.SystemicRegression => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        FailurePattern.CascadingFailures => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        FailurePattern.FlakyTest => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        FailurePattern.ChronicFailure => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        FailurePattern.Resolved => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
        FailurePattern.NewFailure => new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)),
        _ => new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
    };

    public Brush VerdictForegroundBrush => Report.Pattern switch
    {
        FailurePattern.SystemicRegression => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        FailurePattern.CascadingFailures => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        FailurePattern.FlakyTest => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        FailurePattern.ChronicFailure => new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD)),
        FailurePattern.Resolved => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
        FailurePattern.NewFailure => new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)),
        _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
    };

    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Failure Pattern Analysis — {TestCaseName}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Pattern   : {PatternLabel}");
        sb.AppendLine($"Confidence: {ConfidenceText}");
        sb.AppendLine($"Verdict   : {Verdict}");
        sb.AppendLine();
        sb.AppendLine("Build history (newest first):");
        foreach (var h in Report.History)
            sb.AppendLine($"  [{h.Outcome,-6}] {h.BuildName}  ({h.BuildDate:yyyy-MM-dd HH:mm})");
        sb.AppendLine();
        sb.AppendLine("Suggested action:");
        sb.AppendLine(SuggestedAction);
        return sb.ToString();
    }

    /// <summary>One coloured cell in the timeline strip.</summary>
    public sealed class BuildHistoryCell
    {
        public BuildHistoryCell(TestExecutionRecord r)
        {
            BuildName = r.BuildName;
            Outcome = r.Outcome;
            ToolTipText = $"{r.BuildName} — {r.Outcome} ({r.BuildDate:yyyy-MM-dd HH:mm})";
            (OutcomeBrush, OutcomeChar) = r.Outcome switch
            {
                "Passed" => (new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), "\u2713"),
                "Failed" => (new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), "\u2717"),
                _ => (new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), "\u25CB"),
            };
        }

        public string BuildName { get; }
        public string Outcome { get; }
        public string ToolTipText { get; }
        public Brush OutcomeBrush { get; }
        public string OutcomeChar { get; }
    }

    /// <summary>Row in the signature comparison grid.</summary>
    public sealed class SignatureRow
    {
        public SignatureRow(FailureSignature s)
        {
            BuildName = s.BuildName;
            FailedStepName = string.IsNullOrEmpty(s.FailedStepName) ? "—" : s.FailedStepName;
            ErrorType = string.IsNullOrEmpty(s.ErrorType) ? "—" : s.ErrorType;
            NormalizedMessage = s.NormalizedMessage;
            Agent = string.IsNullOrEmpty(s.Agent) ? "—" : s.Agent;
        }

        public string BuildName { get; }
        public string FailedStepName { get; }
        public string ErrorType { get; }
        public string NormalizedMessage { get; }
        public string Agent { get; }
    }
}
