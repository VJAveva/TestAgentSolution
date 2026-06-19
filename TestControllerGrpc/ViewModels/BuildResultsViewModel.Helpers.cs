using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

public partial class BuildResultsViewModel : ObservableObject
{
    // ???????????????????????????????????????????????????????????????
    // Private helpers � Email (consolidated)
    // ???????????????????????????????????????????????????????????????

    private void SendEmail(string? recipients, string subject, string htmlBody, string label)
    {
        if (string.IsNullOrWhiteSpace(recipients) || string.IsNullOrWhiteSpace(_config.FromAddress))
        {
            StatusMessage = $"Configure recipients and FromAddress in appsettings.json BuildResults section.";
            return;
        }

        try
        {
            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, recipients)
            {
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"{label} sent to {recipients}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send {label}: {ex.Message}";
        }
    }

    private void SendReportForBuild(BuildNode node)
    {
        var html = _htmlGenerator.GenerateSingleBuildHtml(node);
        var subject = $"Build Results: {node.BuildNumber} � {node.PassRate:F1}% ({node.Health})";
        SendEmail(_config.ReportRecipients, subject, html, $"Report for {node.BuildNumber}");
    }

    private void SendReportForAllBuilds()
    {
        var html = _htmlGenerator.GenerateMultiBuildHtml(
            LoadedBuildNodes.ToList(), StatTotal, StatPassed, StatFailed, StatTimeout);
        var subject = $"All Build Results: {LoadedBuildNodes.Count} builds � {StatPassed}/{StatTotal} passed";
        SendEmail(_config.ReportRecipients, subject, html, "Aggregate report");
    }

    private void ExportBuildToHtml(BuildNode node)
    {
        var html = _htmlGenerator.GenerateSingleBuildHtml(node);
        var path = Path.Combine(Path.GetTempPath(), $"{node.BuildNumber}_Report.html");
        File.WriteAllText(path, html);
        OpenInBrowser(path);
        StatusMessage = $"Opened HTML report for {node.BuildNumber}";
    }

    private void ExportBuildToCsv(BuildNode node)
    {
        var csv = _htmlGenerator.GenerateCsvContent(node);
        var path = Path.Combine(Path.GetTempPath(), $"{node.BuildNumber}_Results.csv");
        File.WriteAllText(path, csv);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusMessage = $"Opened CSV for {node.BuildNumber}";
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Health & stats
    // ???????????????????????????????????????????????????????????????

    private void UpdateHealthIndicator(BuildNode? node)
    {
        if (node is null)
        {
            HealthColor = "#6B7280";
            HealthLabel = "N/A";
            PassRatePercent = 0;
            return;
        }

        PassRatePercent = node.PassRate;
        var health = _aggregator.EvaluateHealth(node.PassRate);
        (HealthColor, HealthLabel) = health switch
        {
            HealthStatus.Good => ("#10B981", "GOOD"),
            HealthStatus.Warning => ("#F59E0B", "WARNING"),
            _ => ("#EF4444", "BAD"),
        };
    }

    private void UpdateAggregateHealth()
    {
        var aggRate = StatTotal > 0 ? (double)StatPassed / StatTotal * 100 : 0;
        PassRatePercent = aggRate;
        var health = _aggregator.EvaluateHealth(aggRate);
        (HealthColor, HealthLabel) = health switch
        {
            HealthStatus.Good => ("#10B981", "GOOD"),
            HealthStatus.Warning => ("#F59E0B", "WARNING"),
            _ => ("#EF4444", "BAD"),
        };
    }

    private void UpdateAggregateStats()
    {
        StatTotal = LoadedBuildNodes.Sum(b => b.TotalTests);
        StatPassed = LoadedBuildNodes.Sum(b => b.PassedTests);
        StatFailed = LoadedBuildNodes.Sum(b => b.FailedTests);
        StatTimeout = LoadedBuildNodes.Sum(b => b.TimeoutTests);
    }

    private void SyncThresholdsToConfig()
    {
        _config.GoodThreshold = GoodThreshold;
        _config.WarningThreshold = WarningThreshold;
    }

    private string GetRateColor(double rate) =>
        rate > GoodThreshold ? "#10B981"
        : rate >= WarningThreshold ? "#F59E0B"
        : "#EF4444";

    private static string TruncateError(string? msg) =>
        msg is null ? "" : msg.Length > 120 ? msg[..120] + "�" : msg;

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Expand state & tree colors
    // ???????????????????????????????????????????????????????????????

    private bool GetExpandState(string key, bool defaultValue) =>
        _expandState.TryGetValue(key, out var val) ? val : defaultValue;

    private void SetExpandState(string key, bool value) =>
        _expandState[key] = value;

    private void RefreshTreeColors(ObservableCollection<ResultsTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.PassRate.HasValue)
                node.PassRateColor = GetRateColor(node.PassRate.Value);
            RefreshTreeColors(node.Children);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Consecutive failure detection
    // ???????????????????????????????????????????????????????????????

    private async Task RunConsecutiveFailureDetection()
    {
        if (!_config.AlertOnLoad || string.IsNullOrWhiteSpace(ResultsRootPath)) return;

        try
        {
            var threshold = _config.ConsecutiveFailThreshold > 0 ? _config.ConsecutiveFailThreshold : 2;
            var detector = new ConsecutiveFailureDetector();
            var alerts = await Task.Run(() => detector.Detect(ResultsRootPath, _parser, threshold));

            FailureAlerts = new ObservableCollection<ConsecutiveFailureAlert>(alerts);
            AlertStatusText = alerts.Count > 0
                ? $"\u26A0 {alerts.Count} test(s) need investigation"
                : "";
        }
        catch
        {
            // Silent on auto-detect failure
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Consolidated time range
    // ???????????????????????????????????????????????????????????????

    private void ApplyTimeRangeFilter()
    {
        var cutoff = GetTimeRangeCutoff();
        var matching = AvailableBuilds.Where(b => b.ModifiedDate >= cutoff).ToList();
        FilteredBuildCount = matching.Count;

        var rangeLabel = GetRangeLabel(abbreviated: false);
        ScopeSummaryText = matching.Count > 0
            ? $"Showing consolidated data for {matching.Count} build(s) over the {rangeLabel}."
            : $"No builds found in the {rangeLabel}.";

        ConsolidatedFolderSummary = matching.Count > 0
            ? $"Folders in range ({rangeLabel}):\n" + string.Join("\n", matching.Select(b => $"  � {b.BuildNumber}  ({b.ModifiedDate:yyyy-MM-dd HH:mm})"))
            : $"No folders in the {rangeLabel}.";

        StatusMessage = ScopeSummaryText;
    }

    private DateTime GetTimeRangeCutoff() => SelectedTimeRange switch
    {
        TimeRangeFilter.OneDay => DateTime.Now.AddDays(-1),
        TimeRangeFilter.OneWeek => DateTime.Now.AddDays(-7),
        TimeRangeFilter.OneMonth => DateTime.Now.AddDays(-30),
        _ => DateTime.MinValue
    };

    private string GetRangeLabel(bool abbreviated) => SelectedTimeRange switch
    {
        TimeRangeFilter.OneDay => abbreviated ? "24h" : "last 24 hours",
        TimeRangeFilter.OneWeek => abbreviated ? "7d" : "last 7 days",
        TimeRangeFilter.OneMonth => abbreviated ? "30d" : "last 30 days",
        _ => "custom range"
    };

    // ???????????????????????????????????????????????????????????????
    // Private helpers � Shared build parsing & browser launch
    // ???????????????????????????????????????????????????????????????

    private async Task ParseBuildsAsync(IEnumerable<BuildListItem> builds)
    {
        var buildList = builds.ToList();
        var total = buildList.Count;
        var done = 0;

        // Collect results in a thread-safe bag, then update UI in one batch
        var parsedNodes = new ConcurrentBag<(BuildListItem Build, BuildNode Node)>();

        var semaphore = new SemaphoreSlim(4);
        var tasks = buildList.Select(async build =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!Directory.Exists(build.Path))
                {
                    var current = Interlocked.Increment(ref done);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                        StatusMessage = $"Skipped {build.BuildNumber} (folder no longer exists) [{current}/{total}]");
                    return;
                }

                BuildNode node;
                if (_buildCache.TryGetValue(build.Path, out var cached))
                {
                    node = cached;
                }
                else
                {
                    node = await Task.Run(() => _parser.ParseBuildFolder(build.Path));
                    node = _aggregator.EvaluateBuildHealth(node);
                    _buildCache[build.Path] = node;
                }

                parsedNodes.Add((build, node));

                var current2 = Interlocked.Increment(ref done);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    StatusMessage = $"Parsing builds: {current2}/{total} ({node.BuildNumber}: {node.TotalTests} tests)");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref done);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    StatusMessage = $"Skipped {build.BuildNumber}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Prune cache entries for folders that no longer exist
        var validPaths = new HashSet<string>(buildList.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _buildCache.Keys)
        {
            if (!validPaths.Contains(key))
                _buildCache.TryRemove(key, out _);
        }

        // Sort and populate LoadedBuildNodes on the UI thread in one batch
        var sorted = parsedNodes
            .Select(p => p.Node)
            .OrderByDescending(b => b.LatestRun)
            .ToList();

        LoadedBuildNodes.Clear();
        foreach (var n in sorted)
            LoadedBuildNodes.Add(n);

        foreach (var (build, _) in parsedNodes)
            build.HasBeenLoaded = true;
    }

    [RelayCommand]
    private void ClearCache()
    {
        _buildCache.Clear();
        StatusMessage = "Cache cleared. Next LoadAll will re-parse all builds.";
    }

    private static void OpenInBrowser(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msedge.exe",
                Arguments = $"\"{filePath}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
    }
}
