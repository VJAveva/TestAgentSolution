using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

/// <summary>One classified row, flattened for the Detail pane's step table.</summary>
public sealed class FirRowVM
{
    public string Tag { get; init; } = "";
    public string Row { get; init; } = "";
    public string Step { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Why { get; init; } = "";
}

public partial class BuildResultsViewModel
{
    /// <summary>Guards against a slow report landing after the user has moved to another test.</summary>
    private int _firGeneration;

    [ObservableProperty] private bool _hasFir;
    [ObservableProperty] private string _firVerdict = "";
    [ObservableProperty] private string _firSummary = "";
    [ObservableProperty] private string _firWhere = "";
    [ObservableProperty] private string _firSource = "";
    [ObservableProperty] private string _firImpact = "";
    [ObservableProperty] private string _firCategories = "";
    [ObservableProperty] private string _firOpenPoint = "";
    [ObservableProperty] private string _firSecretWarning = "";
    [ObservableProperty] private ObservableCollection<FirRowVM> _firRows = new();

    /// <summary>Raw TRX sections start collapsed behind the report, and open when there is no report.</summary>
    [ObservableProperty] private bool _showRawOutput = true;

    [ObservableProperty] private string _rawOutputToggleText = "Hide raw output";

    partial void OnShowRawOutputChanged(bool value)
        => RawOutputToggleText = value ? "Hide raw output" : "Show raw output";

    [RelayCommand]
    private void ToggleRawOutput() => ShowRawOutput = !ShowRawOutput;

    [RelayCommand]
    private void CopyFirAsBugText()
    {
        if (!HasFir) return;

        var text = string.Join(Environment.NewLine,
            new[] { FirSummary, "" }
                .Concat(FirRows.Select(r => $"{r.Tag,-10} row {r.Row,-4} {r.Step} - {r.Evidence}")));

        try
        {
            Clipboard.SetText(text);
            StatusMessage = "Report copied as bug text.";
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard; that is not worth an error dialog.
            StatusMessage = $"Could not copy: {ex.Message}";
        }
    }

    private void ClearFir()
    {
        HasFir = false;
        FirVerdict = FirSummary = FirWhere = FirSource = FirImpact = "";
        FirCategories = FirOpenPoint = FirSecretWarning = "";
        FirRows.Clear();
        ShowRawOutput = true;
    }

    /// <summary>
    /// Computed off the UI thread: the first call for a test reads the TRX, maybe a log and a workbook.
    /// Returns the task so tests can await it; the UI fires and forgets.
    /// </summary>
    internal Task PopulateFir(string buildNumber, TestResult test)
    {
        ClearFir();

        // Bumped before the early returns: selecting ANY other test, including a passing one, must
        // invalidate a report still being computed for the previous selection.
        var generation = ++_firGeneration;

        if (_firService is null || string.IsNullOrWhiteSpace(buildNumber)) return Task.CompletedTask;
        if (!FirAnalyzer.IsFailure(test.Outcome)) return Task.CompletedTask;

        var testName = test.TestName;

        // FromCurrentSynchronizationContext throws when there is none, which is every non-UI caller.
        var scheduler = SynchronizationContext.Current is not null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Current;

        return Task.Run(() => _firService.GetOrCompute(buildNumber, testName))
            .ContinueWith(task =>
            {
                if (generation != _firGeneration) return;
                Apply(task.Status == TaskStatus.RanToCompletion ? task.Result : null);
            }, scheduler);
    }

    private void Apply(FirReport? report)
    {
        if (report is null)
        {
            ClearFir();
            return;
        }

        FirVerdict = report.Verdict;
        FirSummary = report.Summary;
        FirWhere = report.Root is { } root
            ? $"Row {root.Row} \u00B7 {root.Keyword}"
            : report.FailureType.ToString();
        FirSource = report.Root?.SourceFileLine ?? report.FailingFrame?.SourceFileLine ?? "";
        FirImpact = $"{report.FailedSteps} failed step(s)"
                    + (report.Reason is { } reason
                        ? $" \u00B7 {reason.LoggerErrors} logger error(s), {reason.LoggerWarnings} warning(s)"
                        : "");

        FirCategories = string.Join("   ", report.Categories.Select(c => $"{c.Name}: {c.Count}"));
        if (report.NoiseLines > 0)
            FirCategories += $"   Harness noise hidden: {report.NoiseLines}";

        FirOpenPoint = report.OpenPoint ?? report.DataWarning ?? "";
        FirSecretWarning = report.SecretsFound > 0
            ? $"This log printed credentials ({report.SecretsFound} line(s)). They are hidden here; fix the "
              + "keyword's logging and rotate the account."
            : "";

        FirRows = new ObservableCollection<FirRowVM>(report.Rows.Select(r => new FirRowVM
        {
            Tag = r.IsSlow && r.Tag == FirRowTag.Cascade ? "SLOW" : r.Tag.ToString().ToUpperInvariant(),
            Row = r.Row.ToString(),
            Step = r.DataStep?.Display ?? r.Keyword,
            Evidence = r.Evidence,
            Why = r.Why,
        }));

        HasFir = true;
        ShowRawOutput = false;
    }
}
