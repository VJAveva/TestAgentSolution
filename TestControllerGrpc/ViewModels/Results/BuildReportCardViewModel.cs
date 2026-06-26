using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Results;

/// <summary>
/// Top-level view model for the Build Report Card. Aggregates TRX results on a
/// background thread (never blocks the UI) and exposes the six report sections
/// for binding. Read-only assessment view — actions (email/export/copy) link out.
/// </summary>
public sealed partial class BuildReportCardViewModel : ObservableObject
{
    private readonly BuildReportAggregator _aggregator;
    private readonly BuildReportCardConfig _config;
    private readonly BuildResultsConfig _resultsConfig;
    private readonly IAppLogger _logger;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private BuildReportCard _card = new();
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string? _selectedBuild;

    public ObservableCollection<string> AvailableBuilds { get; } = [];
    public ObservableCollection<CiResult> Cis { get; } = [];
    public ObservableCollection<AgentResult> Agents { get; } = [];
    public ObservableCollection<PsrResult> Psrs { get; } = [];
    public ObservableCollection<FailureEntry> Failures { get; } = [];
    public ObservableCollection<TrendPoint> Trend { get; } = [];

    public BuildReportCardViewModel(
        BuildReportAggregator aggregator,
        BuildReportCardConfig config,
        BuildResultsConfig resultsConfig,
        IAppLogger logger)
    {
        _aggregator = aggregator;
        _config = config;
        _resultsConfig = resultsConfig;
        _logger = logger;
    }

    /// <summary>Joined grade breakdown for the grade-circle tooltip (RC-04).</summary>
    public string GradeBreakdownText => string.Join("\n", Card.Grade.BreakdownLines);

    /// <summary>Re-aggregates when the user picks a different build.</summary>
    partial void OnSelectedBuildChanged(string? value)
    {
        if (_suppressSelectionReload || string.IsNullOrWhiteSpace(value)) return;
        _ = RefreshAsync();
    }

    private bool _suppressSelectionReload;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "Aggregating results…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Populate the build picker from the ReportResults root.
            var builds = await Task.Run(() => _aggregator.ListBuilds());
            _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "BuildReportCard",
                $"Listed {builds.Count} build(s)", SelectedBuild, sw.ElapsedMilliseconds);
            _suppressSelectionReload = true;
            AvailableBuilds.Clear();
            foreach (var b in builds) AvailableBuilds.Add(b);
            if ((string.IsNullOrWhiteSpace(SelectedBuild) || !builds.Contains(SelectedBuild)) && builds.Count > 0)
                SelectedBuild = builds[0];
            _suppressSelectionReload = false;

            var swAgg = System.Diagnostics.Stopwatch.StartNew();
            var card = await _aggregator.AggregateAsync(SelectedBuild);
            swAgg.Stop();
            _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "BuildReportCard",
                $"AggregateAsync returned for build '{SelectedBuild}'", SelectedBuild, swAgg.ElapsedMilliseconds);
            Card = card;
            HasData = card.HasData;

            _suppressSelectionReload = true;
            SelectedBuild = card.BuildNumber;
            _suppressSelectionReload = false;

            var swBind = System.Diagnostics.Stopwatch.StartNew();
            Replace(Cis, card.Cis);
            Replace(Agents, card.Agents);
            Replace(Psrs, card.Psrs);
            Replace(Failures, card.Failures);
            Replace(Trend, card.Trend);
            swBind.Stop();
            _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "BuildReportCard",
                $"UI bind complete ({card.Agents.Count} agents, {card.Failures.Count} failures)",
                SelectedBuild, swBind.ElapsedMilliseconds);

            OnPropertyChanged(nameof(GradeBreakdownText));

            StatusMessage = card.HasData
                ? $"Grade {card.Grade.Letter} · {card.PassRate:0.0}% · {card.TotalTests} tests"
                : $"No TRX results found under {_config.ReportResultsRoot}";
        }
        catch (Exception ex)
        {
            _logger.Error("BuildReportCard", "Failed to aggregate report card", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "BuildReportCard",
                $"RefreshAsync total elapsed for build '{SelectedBuild}'", SelectedBuild, sw.ElapsedMilliseconds);
            IsLoading = false;
            _suppressSelectionReload = false;
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        try
        {
            System.Windows.Clipboard.SetText($"{Card.BuildNumber} — {_config.ReportResultsRoot}");
            StatusMessage = "Report location copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    // ── Email the report card to all product owners (RC-Email) ──────────
    [RelayCommand(CanExecute = nameof(CanEmailReport))]
    private void EmailReport()
    {
        var recipients = ResolveOwnerRecipients();
        if (string.IsNullOrWhiteSpace(recipients) || string.IsNullOrWhiteSpace(_resultsConfig.FromAddress))
        {
            StatusMessage = "Configure product-owner emails (BuildReportCard:Owners) and BuildResults:FromAddress in appsettings.json.";
            return;
        }

        try
        {
            var html = BuildReportEmailHtml(Card);
            var subject = $"Build Report Card: {Card.BuildNumber} — Grade {Card.Grade.Letter} ({Card.PassRate:0.0}%)";

            using var smtp = new System.Net.Mail.SmtpClient(_resultsConfig.SmtpServer, _resultsConfig.SmtpPort)
            {
                UseDefaultCredentials = true,
            };
            using var message = new System.Net.Mail.MailMessage(_resultsConfig.FromAddress, recipients)
            {
                Subject = subject,
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"Report card emailed to {recipients}";
            _logger.Info("BuildReportCard", $"Report card for '{Card.BuildNumber}' emailed to {recipients}");
        }
        catch (Exception ex)
        {
            _logger.Error("BuildReportCard", "Failed to email report card", ex);
            StatusMessage = $"Failed to email report card: {ex.Message}";
        }
    }

    private bool CanEmailReport() => HasData && !IsLoading;

    partial void OnHasDataChanged(bool value) => EmailReportCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value) => EmailReportCommand.NotifyCanExecuteChanged();

    /// <summary>Distinct, comma-separated product-owner emails; falls back to BuildResults:ReportRecipients.</summary>
    private string ResolveOwnerRecipients()
    {
        var owners = _config.Owners.Values
            .Select(o => o.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return owners.Count > 0
            ? string.Join(",", owners)
            : _resultsConfig.ReportRecipients;
    }

    /// <summary>Builds a self-contained HTML email body summarising the report card.</summary>
    private static string BuildReportEmailHtml(BuildReportCard card)
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var sb = new System.Text.StringBuilder();
        sb.Append("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#1c2333;margin:0;padding:16px;\">");
        sb.Append("<div style=\"max-width:760px;margin:0 auto;border:1px solid #e2e5ee;border-radius:8px;overflow:hidden;\">");

        // Hero
        sb.Append("<div style=\"background:linear-gradient(135deg,#1a2236,#0f1119);color:#e4e6f0;padding:20px 22px;\">");
        sb.Append("<div style=\"font-size:11px;letter-spacing:1px;color:#9099b8;\">BUILD REPORT CARD</div>");
        sb.Append($"<div style=\"font-size:24px;font-weight:600;margin:4px 0;\">{Esc(card.BuildNumber)}</div>");
        sb.Append($"<div style=\"font-size:12px;color:#9099b8;\">Grade {Esc(card.Grade.Letter)} · {card.PassRate:0.0}% · {card.TotalTests} tests · {Esc(card.Grade.Verdict)}</div>");
        sb.Append($"<div style=\"font-size:12px;color:#9099b8;margin-top:4px;\">Triggered by {Esc(card.TriggeredBy)} · Duration {card.Duration:hh\\:mm\\:ss}</div>");
        sb.Append("</div>");

        // Summary
        sb.Append("<div style=\"padding:16px 22px;\">");
        sb.Append($"<p style=\"margin:0 0 12px;\"><b>{card.PassedTests}</b> pass · <b>{card.FailedTests}</b> fail · <b>{card.SkippedTests}</b> skip · ");
        sb.Append($"<b>{card.RegressionCount}</b> regressions · <b>{card.FlakyCount}</b> flaky · <b>{card.PsrErrorCount}</b> PSR err</p>");

        // CI table
        if (card.Cis.Count > 0)
        {
            sb.Append("<h3 style=\"margin:12px 0 6px;font-size:14px;\">Configuration Items</h3>");
            sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:12px;\">");
            sb.Append("<tr style=\"background:#f4f6fa;text-align:left;\"><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">CI</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Pass</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Fail</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Skip</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Rate</th></tr>");
            foreach (var ci in card.Cis)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{Esc(ci.Name)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{ci.Passed}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{ci.Failed}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{ci.Skipped}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{ci.PassRate:0.0}%</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        // Top failures
        if (card.Failures.Count > 0)
        {
            sb.Append("<h3 style=\"margin:16px 0 6px;font-size:14px;\">Top Failures</h3>");
            sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:12px;\">");
            sb.Append("<tr style=\"background:#f4f6fa;text-align:left;\"><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Test</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">CI</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Pattern</th><th style=\"padding:6px 8px;border:1px solid #e2e5ee;\">Owner</th></tr>");
            foreach (var f in card.Failures)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{Esc(f.TestName)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{Esc(f.Ci)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{Esc(f.PatternLabel)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border:1px solid #e2e5ee;\">{Esc(f.Owner)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }
}