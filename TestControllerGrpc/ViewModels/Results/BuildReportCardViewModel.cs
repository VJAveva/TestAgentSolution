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
    public RangeObservableCollection<CiResult> Cis { get; } = [];
    public RangeObservableCollection<AgentResult> Agents { get; } = [];
    public RangeObservableCollection<PsrResult> Psrs { get; } = [];
    public RangeObservableCollection<FailureEntry> Failures { get; } = [];
    public RangeObservableCollection<TrendPoint> Trend { get; } = [];

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

    // ── Export the report card as a self-contained HTML file (RC-Export) ──
    // HTML (not PDF) keeps the export dependency-free; the saved file is fully
    // styled and can be printed to PDF from any browser (Ctrl+P → Save as PDF).
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportHtml()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Build Report Card",
                Filter = "HTML report (*.html)|*.html",
                DefaultExt = ".html",
                FileName = $"ReportCard_{SafeFileName(Card.BuildNumber)}.html",
            };
            if (dialog.ShowDialog() != true) return;

            var html = BuildReportEmailHtml(Card);
            System.IO.File.WriteAllText(dialog.FileName, html, System.Text.Encoding.UTF8);
            StatusMessage = $"Report card exported to {dialog.FileName}";
            _logger.Info("BuildReportCard", $"Report card for '{Card.BuildNumber}' exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.Error("BuildReportCard", "Failed to export report card", ex);
            StatusMessage = $"Failed to export report card: {ex.Message}";
        }
    }

    private bool CanExportReport() => HasData && !IsLoading;

    private static string SafeFileName(string raw)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string((raw ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "build" : cleaned;
    }

    private static void Replace<T>(RangeObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        target.AddRange(source);
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

    partial void OnHasDataChanged(bool value)
    {
        EmailReportCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        EmailReportCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

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

    // ── Report-card email palette (mirrors WebClient --rc-* dark tokens) ──
    // Email clients don't support CSS variables, flexbox or grid, so the
    // WebClient look is reproduced with inline styles and table layouts.
    private const string RcBgPrimary = "#0F1119";
    private const string RcBgSecondary = "#171B27";
    private const string RcBgTertiary = "#1F2535";
    private const string RcBorder = "#2A2F45";
    private const string RcTextPrimary = "#E4E6F0";
    private const string RcTextSecondary = "#9099B8";
    private const string RcTextTertiary = "#5E6580";
    private const string RcSuccess = "#22C55E";
    private const string RcBgSuccess = "#0E2818";
    private const string RcInfo = "#4D9CF5";
    private const string RcBgInfo = "#0E1F3A";
    private const string RcWarning = "#F59E0B";
    private const string RcBgWarning = "#2D2418";
    private const string RcDanger = "#EF4444";
    private const string RcBgDanger = "#2D1818";

    private static string SevText(ReportSeverity sev) => sev switch
    {
        ReportSeverity.Pass => RcSuccess,
        ReportSeverity.Info => RcInfo,
        ReportSeverity.Warn => RcWarning,
        ReportSeverity.Fail => RcDanger,
        _ => RcTextSecondary,
    };

    private static string SevBg(ReportSeverity sev) => sev switch
    {
        ReportSeverity.Pass => RcBgSuccess,
        ReportSeverity.Info => RcBgInfo,
        ReportSeverity.Warn => RcBgWarning,
        ReportSeverity.Fail => RcBgDanger,
        _ => RcBgSecondary,
    };

    /// <summary>
    /// Builds a self-contained dark-themed HTML email that mirrors the WebClient
    /// Report Card (hero + grade circle, KPI strip, CI grid, agent execution,
    /// PSR + trend, top failures). Uses table layout + inline styles for broad
    /// email-client compatibility (Outlook, Gmail, web).
    /// </summary>
    private static string BuildReportEmailHtml(BuildReportCard card)
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";

        var mono = "Consolas,'Courier New',monospace";
        var sans = "'Segoe UI',Arial,sans-serif";
        var triggered = (card.StartedUtc ?? card.GeneratedUtc).ToLocalTime().ToString("g");

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>");
        sb.Append($"<body style=\"margin:0;padding:20px;background:{RcBgPrimary};font-family:{sans};\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:820px;margin:0 auto;\"><tr><td>");

        // ── Hero card ────────────────────────────────────────────────────
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid {RcBorder};border-radius:8px;overflow:hidden;\">");
        sb.Append($"<tr><td style=\"background:linear-gradient(135deg,#1a2236 0%,{RcBgPrimary} 100%);background-color:#1a2236;padding:22px 24px;\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        // left: build identity
        sb.Append("<td style=\"vertical-align:top;\">");
        sb.Append($"<div style=\"font-size:11px;letter-spacing:1px;text-transform:uppercase;color:{RcTextTertiary};font-weight:600;margin-bottom:6px;\">Build Report Card</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:24px;font-weight:600;color:{RcTextPrimary};margin-bottom:4px;\">{Esc(card.BuildNumber)}</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:11px;color:{RcTextSecondary};\">Triggered: {Esc(triggered)} by {Esc(card.TriggeredBy)} · Duration: {Esc(Dur(card.Duration))}</div>");
        sb.Append("</td>");
        // right: grade circle + verdict
        sb.Append("<td align=\"right\" style=\"vertical-align:top;white-space:nowrap;\">");
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.Append($"<td style=\"width:84px;height:84px;border:2px solid {SevText(card.Grade.Severity)};background:{SevBg(card.Grade.Severity)};border-radius:42px;text-align:center;vertical-align:middle;\">");
        sb.Append($"<div style=\"font-size:34px;font-weight:700;line-height:1;color:{SevText(card.Grade.Severity)};\">{Esc(card.Grade.Letter)}</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:10px;color:{SevText(card.Grade.Severity)};margin-top:2px;\">{card.Grade.Score:0.0}%</div>");
        sb.Append("</td>");
        sb.Append("<td style=\"padding-left:14px;text-align:left;vertical-align:middle;\">");
        sb.Append($"<div style=\"font-size:13px;font-weight:600;color:{SevText(card.Grade.Severity)};margin-bottom:4px;\">{Esc(card.Grade.Verdict)}</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:10px;color:{RcTextTertiary};\">{card.PassedTests:N0} pass / {card.FailedTests:N0} fail / {card.SkippedTests:N0} skip</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:10px;color:{RcTextTertiary};margin-top:2px;\">{card.RegressionCount} regressions · {card.FlakyCount} flaky · {card.PsrErrorCount} PSR error</div>");
        if (card.DeltaVsLast is { } d)
        {
            var dc = d < 0 ? RcDanger : RcSuccess;
            var arrow = d < 0 ? "▼" : "▲";
            sb.Append($"<div style=\"font-family:{mono};font-size:10px;color:{RcTextTertiary};margin-top:4px;\">vs last build: <span style=\"color:{dc};\">{arrow} {Math.Abs(d):0.0}%</span></div>");
        }
        sb.Append("</td></tr></table>");
        sb.Append("</td></tr></table>");
        sb.Append("</td></tr>");

        // ── KPI strip ────────────────────────────────────────────────────
        var failedCis = card.Cis.Count(c => c.Failed > 0);
        sb.Append($"<tr><td style=\"border-top:1px solid {RcBorder};\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        AppendKpi(sb, mono, "Total tests", card.TotalTests.ToString("N0"), $"across {card.Cis.Count} CIs", RcTextPrimary);
        AppendKpi(sb, mono, "Pass rate", $"{card.PassRate:0.0}%", "target: 98%", SevText(card.Grade.Severity));
        AppendKpi(sb, mono, "Failures", card.FailedTests.ToString(), $"in {failedCis} of {card.Cis.Count} CIs", card.FailedTests > 0 ? RcDanger : RcTextPrimary);
        AppendKpi(sb, mono, "Regressions", card.RegressionCount.ToString(), "passed previously", card.RegressionCount > 0 ? RcDanger : RcTextPrimary);
        AppendKpi(sb, mono, "Agents", $"{card.Agents.Count}/{card.Agents.Count}", "all completed", RcTextPrimary);
        AppendKpi(sb, mono, "PSR runs", $"{card.PsrPassCount}/{card.PsrTotalCount}", card.PsrPassCount < card.PsrTotalCount ? $"{card.PsrTotalCount - card.PsrPassCount} failed" : "all passed", card.PsrPassCount < card.PsrTotalCount ? RcWarning : RcTextPrimary, last: true);
        sb.Append("</tr></table>");
        sb.Append("</td></tr></table>");

        // ── CI results grid ──────────────────────────────────────────────
        if (card.Cis.Count > 0)
        {
            AppendSectionOpen(sb, "CI Results · pass rate per Configuration Item", $"{card.Cis.Count} CIs");
            sb.Append("<tr><td style=\"padding:10px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            var col = 0;
            foreach (var ci in card.Cis)
            {
                if (col == 3) { sb.Append("</tr><tr>"); col = 0; }
                sb.Append($"<td width=\"33%\" style=\"padding:4px;vertical-align:top;\"><div style=\"border:1px solid {SevText(ci.Severity)};background:{SevBg(ci.Severity)};border-radius:6px;padding:9px 10px;\">");
                sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
                sb.Append($"<td style=\"font-family:{mono};font-size:11px;font-weight:600;color:{RcTextPrimary};\">{Esc(ci.Name)}</td>");
                sb.Append($"<td align=\"right\"><span style=\"display:inline-block;width:8px;height:8px;border-radius:4px;background:{SevText(ci.Severity)};\">&nbsp;</span></td>");
                sb.Append("</tr></table>");
                sb.Append($"<div style=\"font-family:{mono};font-size:10px;color:{RcTextSecondary};margin-top:4px;\">{ci.Passed} / {ci.Total} · {ci.PassRate:0.0}%</div>");
                sb.Append(Bar(Math.Min(ci.PassRate, 100), SevText(ci.Severity), RcBgTertiary));
                sb.Append("</div></td>");
                col++;
            }
            while (col is > 0 and < 3) { sb.Append("<td width=\"33%\"></td>"); col++; }
            sb.Append("</tr></table></td></tr></table>");
        }

        // ── Agent execution ──────────────────────────────────────────────
        if (card.Agents.Count > 0)
        {
            AppendSectionOpen(sb, $"Agent Execution · {card.Agents.Count} agents · per-agent results", "use case + agent");
            sb.Append("<tr><td>");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
            sb.Append($"<tr style=\"background:{RcBgSecondary};\">");
            foreach (var (h, r) in new[] { ("Use Case", false), ("Agent", false), ("Pass", true), ("Fail", true), ("Skip", true), ("Time", true), ("%", true) })
                sb.Append($"<th style=\"padding:8px 10px;text-align:{(r ? "right" : "left")};font-size:9px;text-transform:uppercase;letter-spacing:.5px;color:{RcTextTertiary};border-bottom:1px solid {RcBorder};font-weight:600;\">{h}</th>");
            sb.Append("</tr>");
            foreach (var a in card.Agents)
            {
                var tint = a.Severity == ReportSeverity.Fail ? RcBgDanger : a.Severity == ReportSeverity.Warn ? RcBgWarning : "transparent";
                sb.Append($"<tr style=\"background:{tint};\">");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextPrimary};font-weight:600;border-bottom:1px solid {RcBorder};\">{Esc(a.UseCase)}</td>");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextPrimary};border-bottom:1px solid {RcBorder};\"><span style=\"display:inline-block;width:6px;height:6px;border-radius:3px;background:{SevText(a.Severity)};\">&nbsp;</span> {Esc(a.AgentName)}</td>");
                sb.Append($"<td align=\"right\" style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{a.Passed}</td>");
                sb.Append($"<td align=\"right\" style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{(a.Failed > 0 ? RcDanger : RcTextSecondary)};border-bottom:1px solid {RcBorder};\">{a.Failed}</td>");
                sb.Append($"<td align=\"right\" style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{a.Skipped}</td>");
                sb.Append($"<td align=\"right\" style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{Esc(Dur(a.Duration))}</td>");
                sb.Append($"<td align=\"right\" style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{SevText(a.Severity)};border-bottom:1px solid {RcBorder};\">{a.PassRate:0.0}%</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></td></tr></table>");
        }

        // ── PSR validation ───────────────────────────────────────────────
        if (card.Psrs.Count > 0)
        {
            AppendSectionOpen(sb, $"PSR Validation · {card.Psrs.Count} customer scenarios", "production-like runs");
            sb.Append("<tr><td style=\"padding:10px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            var col = 0;
            foreach (var p in card.Psrs)
            {
                var sev = p.Outcome switch
                {
                    PsrOutcome.Passed => ReportSeverity.Pass,
                    PsrOutcome.PassedWithWarnings => ReportSeverity.Warn,
                    PsrOutcome.Failed => ReportSeverity.Fail,
                    _ => ReportSeverity.Info,
                };
                var label = p.Outcome switch
                {
                    PsrOutcome.Passed => "PASSED",
                    PsrOutcome.PassedWithWarnings => "PASS w/ WARN",
                    PsrOutcome.Failed => "FAILED",
                    _ => "PENDING",
                };
                if (col == 2) { sb.Append("</tr><tr>"); col = 0; }
                sb.Append($"<td width=\"50%\" style=\"padding:4px;vertical-align:top;\"><div style=\"border:1px solid {SevText(sev)};background:{SevBg(sev)};border-radius:6px;padding:10px;\">");
                sb.Append($"<div style=\"font-size:11px;font-weight:600;color:{RcTextPrimary};margin-bottom:3px;\">{Esc(p.Name)}</div>");
                sb.Append($"<div style=\"font-family:{mono};font-size:9px;color:{SevText(sev)};margin-bottom:6px;\">{label} · {Esc(Dur(p.Duration))}</div>");
                sb.Append(PsrStat(mono, "Tags", p.Tags.ToString("N0"), false));
                if (!string.IsNullOrEmpty(p.Throughput)) sb.Append(PsrStat(mono, "Throughput", Esc(p.Throughput), false));
                if (p.Warnings > 0) sb.Append(PsrStat(mono, "Warnings", p.Warnings.ToString(), true));
                if (p.Errors > 0) sb.Append(PsrStat(mono, "Errors", Esc(p.ErrorDetail ?? p.Errors.ToString()), true));
                sb.Append("</div></td>");
                col++;
            }
            while (col is > 0 and < 2) { sb.Append("<td width=\"50%\"></td>"); col++; }
            sb.Append("</tr></table></td></tr></table>");
        }

        // ── Trend strip ──────────────────────────────────────────────────
        if (card.Trend.Count > 0)
        {
            AppendSectionOpen(sb, $"Pass rate · last {card.Trend.Count} builds", "trend");
            sb.Append("<tr><td style=\"padding:14px;\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"height:90px;\"><tr style=\"vertical-align:bottom;\">");
            foreach (var t in card.Trend)
            {
                var passH = (int)Math.Round(Math.Max(Math.Min(t.PassRate, 100), 0) * 0.7); // px out of 70
                var failH = 70 - passH;
                var outline = t.IsCurrent ? $"outline:1px solid {RcWarning};" : "";
                sb.Append("<td align=\"center\" style=\"padding:0 3px;vertical-align:bottom;\">");
                sb.Append($"<div style=\"width:20px;{outline}\">");
                if (failH > 0) sb.Append($"<div style=\"height:{failH}px;background:{RcDanger};\"></div>");
                sb.Append($"<div style=\"height:{passH}px;background:{RcSuccess};\"></div>");
                sb.Append("</div>");
                sb.Append($"<div style=\"font-family:{mono};font-size:9px;color:{(t.IsCurrent ? RcWarning : RcTextTertiary)};margin-top:4px;\">{(t.IsCurrent ? "★" : Esc(t.Label))}</div>");
                sb.Append("</td>");
            }
            sb.Append("</tr></table></td></tr></table>");
        }

        // ── Top failures ─────────────────────────────────────────────────
        if (card.Failures.Count > 0)
        {
            AppendSectionOpen(sb, "Top Failures · prioritized for triage", $"{card.FailedTests} total · showing top {card.Failures.Count}");
            sb.Append("<tr><td>");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
            sb.Append($"<tr style=\"background:{RcBgSecondary};\">");
            foreach (var h in new[] { "Test", "CI", "Failed On", "Pattern", "Owner" })
                sb.Append($"<th style=\"padding:8px 10px;text-align:left;font-size:9px;text-transform:uppercase;letter-spacing:.5px;color:{RcTextTertiary};border-bottom:1px solid {RcBorder};font-weight:600;\">{h}</th>");
            sb.Append("</tr>");
            foreach (var f in card.Failures)
            {
                var (tagFg, tagBg) = f.Pattern switch
                {
                    FailurePatternKind.Regression => (RcDanger, RcBgDanger),
                    FailurePatternKind.New => (RcDanger, RcBgDanger),
                    FailurePatternKind.Flaky => (RcWarning, RcBgWarning),
                    FailurePatternKind.Cascading => (RcWarning, RcBgWarning),
                    FailurePatternKind.Resolved => (RcSuccess, RcBgSuccess),
                    _ => (RcTextTertiary, RcBgTertiary),
                };
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextPrimary};font-weight:600;border-bottom:1px solid {RcBorder};\">{Esc(f.TestName)}</td>");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{Esc(f.Ci)}</td>");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{Esc(string.Join(", ", f.FailedOnAgents))}</td>");
                sb.Append($"<td style=\"padding:8px 10px;border-bottom:1px solid {RcBorder};\"><span style=\"display:inline-block;padding:2px 8px;border-radius:9px;font-family:{mono};font-size:9px;font-weight:600;color:{tagFg};background:{tagBg};\">{Esc(f.PatternLabel)}</span></td>");
                sb.Append($"<td style=\"padding:8px 10px;font-family:{mono};font-size:11px;color:{RcTextSecondary};border-bottom:1px solid {RcBorder};\">{Esc(f.Owner)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></td></tr></table>");
        }

        // ── Footer ───────────────────────────────────────────────────────
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:14px;border:1px solid {RcBorder};border-radius:8px;\"><tr>");
        sb.Append($"<td style=\"padding:12px 16px;background:{RcBgSecondary};font-size:10px;color:{RcTextTertiary};\">Report generated {Esc(card.GeneratedUtc.ToLocalTime().ToString("g"))}</td>");
        sb.Append($"<td align=\"right\" style=\"padding:12px 16px;background:{RcBgSecondary};font-family:{mono};font-size:10px;color:{RcTextTertiary};\">Grade {Esc(card.Grade.Letter)} · {card.Grade.Score:0.0}%</td>");
        sb.Append("</tr></table>");

        sb.Append("</td></tr></table></body></html>");
        return sb.ToString();
    }

    private static void AppendKpi(System.Text.StringBuilder sb, string mono, string label, string value, string sub, string valueColor, bool last = false)
    {
        var rb = last ? "" : $"border-right:1px solid {RcBorder};";
        sb.Append($"<td width=\"16%\" style=\"padding:12px 14px;background:{RcBgSecondary};{rb}vertical-align:top;\">");
        sb.Append($"<div style=\"font-size:9px;text-transform:uppercase;letter-spacing:.5px;color:{RcTextTertiary};font-weight:600;margin-bottom:4px;\">{label}</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:17px;font-weight:600;color:{valueColor};\">{value}</div>");
        sb.Append($"<div style=\"font-family:{mono};font-size:9px;color:{RcTextTertiary};margin-top:2px;\">{sub}</div>");
        sb.Append("</td>");
    }

    private static void AppendSectionOpen(System.Text.StringBuilder sb, string left, string right)
    {
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:14px;border:1px solid {RcBorder};border-radius:8px;overflow:hidden;\">");
        sb.Append($"<tr><td style=\"padding:9px 14px;background:{RcBgSecondary};border-bottom:1px solid {RcBorder};\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.Append($"<td style=\"font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:{RcTextTertiary};font-weight:600;\">{System.Net.WebUtility.HtmlEncode(left)}</td>");
        sb.Append($"<td align=\"right\" style=\"font-size:9px;color:{RcTextTertiary};\">{System.Net.WebUtility.HtmlEncode(right)}</td>");
        sb.Append("</tr></table></td></tr>");
    }

    private static string Bar(double pct, string fill, string track)
        => $"<div style=\"margin-top:6px;height:3px;background:{track};border-radius:2px;\"><div style=\"height:3px;width:{pct:0.#}%;background:{fill};border-radius:2px;\"></div></div>";

    private static string PsrStat(string mono, string label, string value, bool bad)
    {
        var color = bad ? RcDanger : RcTextSecondary;
        return $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>" +
               $"<td style=\"font-family:{mono};font-size:9px;color:{color};padding:1px 0;\">{label}</td>" +
               $"<td align=\"right\" style=\"font-family:{mono};font-size:9px;color:{color};padding:1px 0;\">{value}</td>" +
               "</tr></table>";
    }
}