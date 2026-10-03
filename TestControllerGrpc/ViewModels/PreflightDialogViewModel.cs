using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Core.Preflight;

namespace TestControllerGrpc.ViewModels;

/// <summary>One row in the Checks grid.</summary>
public sealed record PreflightCheckRow(
    PreflightStatus Status,
    string Area,
    string Check,
    string Detail,
    string? FixHint)
{
    public string StatusText => Status switch
    {
        PreflightStatus.Fail => "FAIL",
        PreflightStatus.Warn => "WARN",
        _ => "PASS",
    };

    /// <summary>Failures first, then warnings, then passes.</summary>
    public int SortKey => Status switch
    {
        PreflightStatus.Fail => 0,
        PreflightStatus.Warn => 1,
        _ => 2,
    };

    public bool HasFixHint => !string.IsNullOrWhiteSpace(FixHint);
}

/// <summary>One row in the Parameters grid.</summary>
public sealed record PreflightTokenRow(string Token, string Value, string Source);

/// <summary>
/// View model for the pre-flight dialog. Presentation only - it never re-runs or alters a check,
/// it only groups, sorts and filters what <see cref="PreflightRunner"/> already decided.
/// </summary>
public sealed partial class PreflightDialogViewModel : ObservableObject
{
    private readonly Func<PreflightReport>? _recheck;

    public PreflightDialogViewModel(PreflightReport report, string checkedBy, bool checkOnly, Func<PreflightReport>? recheck = null)
    {
        _recheck = recheck;
        CheckOnly = checkOnly;
        CheckedBy = checkedBy;

        Checks = [];
        Tokens = [];
        ChecksView = CollectionViewSource.GetDefaultView(Checks);
        ChecksView.Filter = FilterCheck;
        TokensView = CollectionViewSource.GetDefaultView(Tokens);
        TokensView.Filter = FilterToken;

        Load(report);
    }

    public bool CheckOnly { get; }
    public string CheckedBy { get; }
    public bool CanRecheck => _recheck is not null;

    public ObservableCollection<PreflightCheckRow> Checks { get; }
    public ObservableCollection<PreflightTokenRow> Tokens { get; }
    public ICollectionView ChecksView { get; }
    public ICollectionView TokensView { get; }

    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerDetail = "";
    [ObservableProperty] private PreflightStatus _bannerKind = PreflightStatus.Pass;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _passCount;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private string _runText = "Run";
    [ObservableProperty] private string _reportText = "";

    /// <summary>All | Failed | Warnings | Passed.</summary>
    [ObservableProperty] private string _checkFilter = "All";

    [ObservableProperty] private string _tokenSearch = "";

    public int TokenCount => Tokens.Count;

    partial void OnCheckFilterChanged(string value) => ChecksView.Refresh();
    partial void OnTokenSearchChanged(string value) => TokensView.Refresh();

    /// <summary>Re-runs the checks and rebinds. Returns false when no re-check source was supplied.</summary>
    public bool Recheck()
    {
        if (_recheck is null) return false;
        Load(_recheck());
        return true;
    }

    private void Load(PreflightReport report)
    {
        Target = report.Target;
        ReportText = report.ToPlainText();

        FailedCount = report.CountOf(PreflightStatus.Fail);
        WarnCount = report.CountOf(PreflightStatus.Warn);
        PassCount = report.CountOf(PreflightStatus.Pass);

        Checks.Clear();
        foreach (var c in report.Checks
                     .Select(c => new PreflightCheckRow(c.Status, c.Group, c.Name, c.Detail, c.FixHint))
                     .OrderBy(r => r.SortKey)
                     .ThenBy(r => Array.IndexOf(PreflightGroups.InOrder, r.Area))
                     .ThenBy(r => r.Check, StringComparer.OrdinalIgnoreCase))
        {
            Checks.Add(c);
        }

        Tokens.Clear();
        foreach (var t in report.Tokens.OrderBy(t => t.Token, StringComparer.OrdinalIgnoreCase))
            Tokens.Add(new PreflightTokenRow(t.Token, t.Value, t.SourceLayer));

        CanRun = !CheckOnly && report.CanRun;
        // "Run anyway" is the warnings-only wording. With failures the button is disabled, and
        // labelling a dead button "Run anyway" reads as though the block were a suggestion.
        RunText = !report.HasErrors && report.HasWarnings ? "Run anyway" : "Run";

        BannerKind = report.HasErrors ? PreflightStatus.Fail
            : report.HasWarnings ? PreflightStatus.Warn
            : PreflightStatus.Pass;

        BannerText = BannerKind switch
        {
            PreflightStatus.Fail => $"Blocked \u2014 {FailedCount} failed",
            PreflightStatus.Warn => "Warnings \u2014 review",
            _ => $"Ready to run \u2014 {PassCount} passed",
        };

        BannerDetail =
            $"{FailedCount} failed \u00b7 {WarnCount} warning{(WarnCount == 1 ? "" : "s")} \u00b7 {PassCount} passed"
            + $" \u00b7 checked by {CheckedBy} in {report.Elapsed.TotalSeconds:F1} s";

        // Land on the tab that needs attention, not on a wall of passes.
        CheckFilter = FailedCount > 0 ? "Failed" : WarnCount > 0 ? "Warnings" : "All";
        ChecksView.Refresh();
        TokensView.Refresh();
        OnPropertyChanged(nameof(TokenCount));
    }

    private bool FilterCheck(object item)
    {
        if (item is not PreflightCheckRow row) return false;
        return CheckFilter switch
        {
            "Failed" => row.Status == PreflightStatus.Fail,
            "Warnings" => row.Status == PreflightStatus.Warn,
            "Passed" => row.Status == PreflightStatus.Pass,
            _ => true,
        };
    }

    private bool FilterToken(object item)
    {
        if (item is not PreflightTokenRow row) return false;
        if (string.IsNullOrWhiteSpace(TokenSearch)) return true;

        return row.Token.Contains(TokenSearch, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(TokenSearch, StringComparison.OrdinalIgnoreCase)
            || row.Source.Contains(TokenSearch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Markdown for Save..., so a report can be pasted into a ticket.</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Pre-flight \u2014 {Target}");
        sb.AppendLine();
        sb.AppendLine($"**{BannerText}** \u2014 {BannerDetail}");
        sb.AppendLine();
        sb.AppendLine("| Status | Area | Check | Details / how to fix |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in Checks)
        {
            var details = c.HasFixHint ? $"{c.Detail}<br/>**Fix:** {c.FixHint}" : c.Detail;
            sb.AppendLine($"| {c.StatusText} | {Escape(c.Area)} | {Escape(c.Check)} | {Escape(details)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Parameters");
        sb.AppendLine();
        sb.AppendLine("| Token | Value | Source |");
        sb.AppendLine("|---|---|---|");
        foreach (var t in Tokens)
            sb.AppendLine($"| `[{t.Token}]` | {Escape(t.Value)} | {t.Source} |");

        return sb.ToString();

        static string Escape(string s) => s.Replace("|", "\\|");
    }
}
