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
    // Commands � Build loading
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void RefreshBuilds()
    {
        AvailableBuilds.Clear();
        var builds = _parser.DiscoverBuilds(ResultsRootPath);
        foreach (var b in builds)
            AvailableBuilds.Add(new BuildListItem { BuildNumber = b.BuildNumber, Path = b.Path, ModifiedDate = b.Modified });

        StatusMessage = builds.Count > 0
            ? $"Found {builds.Count} build(s) in {ResultsRootPath}"
            : $"No builds found in {ResultsRootPath}";
    }

    [RelayCommand]
    private async Task LoadBuildResults()
    {
        if (SelectedBuild is null) return;

        IsLoading = true;
        StatusMessage = $"Parsing .trx files for {SelectedBuild.BuildNumber}...";

        try
        {
            var buildNode = await Task.Run(() => _parser.ParseBuildFolder(SelectedBuild.Path));
            buildNode = _aggregator.EvaluateBuildHealth(buildNode);
            CurrentBuildNode = buildNode;

            LoadedBuildNodes.Clear();
            LoadedBuildNodes.Add(buildNode);

            _expandState.Clear();
            _expandState[$"Build|{buildNode.BuildNumber}"] = true;

            UpdateAggregateStats();

            if (buildNode.UseCases.Count == 0)
            {
                StatusMessage = $"No .trx files found in {SelectedBuild.Path}";
                ResultsTree.Clear();
                FlatResultsList.Clear();
                UpdateHealthIndicator(null);
                return;
            }

            SyncThresholdsToConfig();
            BuildResultsTree(buildNode);
            BuildFlatList();
            await RunConsecutiveFailureDetection();

            UpdateHealthIndicator(buildNode);
            SelectedBuild.HasBeenLoaded = true;
            StatusMessage = $"Build {buildNode.BuildNumber}: {buildNode.TotalTests} tests, {buildNode.PassRate:F1}% pass rate ({buildNode.UseCases.Count} use cases)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error parsing results: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadAllBuilds()
    {
        RefreshBuilds();
        if (AvailableBuilds.Count == 0)
        {
            StatusMessage = "No builds found. Set the results root path first.";
            return;
        }

        IsLoading = true;
        LoadedBuildNodes.Clear();
        _expandState.Clear();
        _expandState["AllBuilds"] = true;

        try
        {
            await ParseBuildsAsync(AvailableBuilds);

            CurrentBuildNode = LoadedBuildNodes.FirstOrDefault();
            UpdateAggregateStats();
            SyncThresholdsToConfig();

            BuildMultiBuildResultsTree();
            BuildFlatList();

            UpdateAggregateHealth();
            await RunConsecutiveFailureDetection();

            var aggRate = StatTotal > 0 ? (double)StatPassed / StatTotal * 100 : 0;
            StatusMessage = $"Loaded {LoadedBuildNodes.Count} builds ({StatTotal} tests, {aggRate:F1}% pass rate)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading all builds: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Commands � Export & Report
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ExportToCsv()
    {
        if (!CanExportReport) { StatusMessage = "Report export is not available."; return; }
        if (CurrentBuildNode is null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"{CurrentBuildNode.BuildNumber}_Results.csv",
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, _htmlGenerator.GenerateCsvContent(CurrentBuildNode));
        StatusMessage = $"Exported CSV to {dlg.FileName}";
    }

    [RelayCommand]
    private void ExportToHtml()
    {
        if (!CanExportReport) { StatusMessage = "Report export is not available."; return; }
        if (CurrentBuildNode is null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "HTML files (*.html)|*.html",
            FileName = $"{CurrentBuildNode.BuildNumber}_Report.html",
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, _htmlGenerator.GenerateSingleBuildHtml(CurrentBuildNode));
        StatusMessage = $"Exported HTML report to {dlg.FileName}";
    }

    [RelayCommand]
    private void OpenTrxInExplorer()
    {
        if (!HasDetailSelected)
        {
            StatusMessage = "No test result selected.";
            return;
        }

        var ctx = new BuildReportHtmlGenerator.TestDetailContext(
            DetailTestName, DetailUseCaseName, DetailBuildNumber,
            DetailOutcome, DetailDuration,
            DetailErrorMessage, DetailStackTrace, DetailStdOut, DetailDebugTrace, DetailTrxFilePath,
            DetailExecutionSteps.Select(s =>
                new BuildReportHtmlGenerator.StepInfo(s.StepName, s.Outcome, s.OutcomeIcon, s.DurationText)).ToList());

        var html = _htmlGenerator.GenerateTestDetailHtml(ctx);
        var safeName = string.Join("_", DetailTestName.Split(Path.GetInvalidFileNameChars()));
        if (safeName.Length > 80) safeName = safeName[..80];
        var tempPath = Path.Combine(Path.GetTempPath(), $"TestResult_{safeName}.html");
        File.WriteAllText(tempPath, html, Encoding.UTF8);

        OpenInBrowser(tempPath);
        StatusMessage = "Opened detail report in browser.";
    }

    // ???????????????????????????????????????????????????????????????
    // Commands � Email
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void SendReport()
    {
        if (!CanSendReport) { StatusMessage = "Report sending requires Manager role."; return; }
        if (CurrentBuildNode is null) return;
        var html = _htmlGenerator.GenerateSingleBuildHtml(CurrentBuildNode);
        var subject = $"Build Results: {CurrentBuildNode.BuildNumber} � {CurrentBuildNode.PassRate:F1}% ({CurrentBuildNode.Health})";
        SendEmail(_config.ReportRecipients, subject, html, "Report");
    }

    [RelayCommand]
    private void SendQaAlert()
    {
        if (!CanSendReport) { StatusMessage = "Report sending requires Manager role."; return; }
        if (FailureAlerts.Count == 0) return;
        var html = _htmlGenerator.GenerateAlertEmailHtml(FailureAlerts);
        var subject = $"\u26A0 Priority Investigation Required � {FailureAlerts.Count} tests failing consecutively";
        SendEmail(_config.QaAlertRecipients, subject, html, "QA alert");
    }

    [RelayCommand]
    private async Task SendEmailSummary()
    {
        if (!CanSendReport) { StatusMessage = "Report sending requires Manager role."; return; }
        if (IsConsolidatedMode)
        {
            if (LoadedBuildNodes.Count == 0)
                await LoadConsolidatedBuilds();
            if (LoadedBuildNodes.Count == 0) return;
            SendReportForAllBuilds();
        }
        else
        {
            SendReportCommand.Execute(null);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Commands � Thresholds & Clipboard
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ApplyThresholds()
    {
        SyncThresholdsToConfig();
        PassRateConverter.UpdateThresholds(GoodThreshold, WarningThreshold);
        if (CurrentBuildNode is not null)
        {
            UpdateHealthIndicator(CurrentBuildNode);
            RefreshTreeColors(ResultsTree);
        }
        if (LoadedBuildNodes.Count > 0)
            BuildFlatList();
        StatusMessage = $"Thresholds updated: Good > {GoodThreshold}%, Warning ? {WarningThreshold}%";
    }

    [RelayCommand]
    private void BrowseResultsPath()
    {
        var dlg = new OpenFolderDialog { Title = "Select Test Results Root Folder" };
        if (dlg.ShowDialog() == true)
        {
            ResultsRootPath = dlg.FolderName;
            _config.ResultsRootPath = dlg.FolderName;
            RefreshBuilds();
        }
    }

    [RelayCommand]
    private void CopyFailedTests()
    {
        if (CurrentBuildNode is null || CurrentBuildNode.AllFailedTests.Count == 0) return;

        var text = string.Join(Environment.NewLine, CurrentBuildNode.AllFailedTests.Select(t =>
            $"{t.TrxFileName} | {t.TestName} | {TruncateError(t.ErrorMessage)}"));
        Clipboard.SetText(text);
        StatusMessage = $"Copied {CurrentBuildNode.AllFailedTests.Count} failed test(s) to clipboard.";
    }

    [RelayCommand]
    private void CopyAlertList()
    {
        if (FailureAlerts.Count == 0) return;

        var text = string.Join(Environment.NewLine, FailureAlerts.Select(a =>
            $"{a.Priority} | {a.TestName} | {a.ConsecutiveFailCount} builds | {a.UseCaseName} | {TruncateError(a.LastError)}"));
        Clipboard.SetText(text);
        StatusMessage = $"Copied {FailureAlerts.Count} alert(s) to clipboard.";
    }

    // ???????????????????????????????????????????????????????????????
    // Commands � Trend & Consecutive Failure Detection
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private async Task GenerateTrendReport()
    {
        if (!CanExportReport) { StatusMessage = "Report export is not available."; return; }
        if (string.IsNullOrWhiteSpace(ResultsRootPath))
        {
            StatusMessage = "Set a results root path first.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Generating trend report across builds...";

        try
        {
            var trend = await Task.Run(() =>
            {
                var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
                return analyzer.AnalyzeTrends(ResultsRootPath, _parser);
            });

            var html = _htmlGenerator.GenerateTrendHtml(trend);
            var path = Path.Combine(Path.GetTempPath(), "TestResults_Trend.html");
            File.WriteAllText(path, html);
            OpenInBrowser(path);
            StatusMessage = $"Trend report generated: {trend.Builds.Count} builds analyzed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error generating trend report: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DetectConsecutiveFailures()
    {
        if (string.IsNullOrWhiteSpace(ResultsRootPath))
        {
            StatusMessage = "Set a results root path first.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Scanning for consecutive failures across builds...";

        try
        {
            var threshold = _config.ConsecutiveFailThreshold > 0 ? _config.ConsecutiveFailThreshold : 2;
            var detector = new ConsecutiveFailureDetector();
            var alerts = await Task.Run(() => detector.Detect(ResultsRootPath, _parser, threshold));

            FailureAlerts = new ObservableCollection<ConsecutiveFailureAlert>(alerts);
            AlertStatusText = alerts.Count > 0
                ? $"\u26A0 {alerts.Count} test(s) need investigation"
                : "No consecutive failures detected.";
            StatusMessage = AlertStatusText;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error detecting failures: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadFlakyTests()
    {
        if (string.IsNullOrWhiteSpace(ResultsRootPath))
        {
            StatusMessage = "Set a results root path first.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Analyzing flaky tests...";

        try
        {
            var detector = new FlakyTestDetector();
            var alerts = await Task.Run(() =>
                detector.DetectFlakyTests(ResultsRootPath, _parser, recentBuilds: 5, minFailures: 2));

            FlakyTests = new ObservableCollection<FlakyTestAlert>(alerts);

            var consistent = alerts.Count(a => a.Classification == "Consistent");
            var frequent = alerts.Count(a => a.Classification == "Frequent");
            var intermittent = alerts.Count(a => a.Classification == "Intermittent");
            FlakyTestSummary = $"{alerts.Count} flaky tests: {consistent} consistent, {frequent} frequent, {intermittent} intermittent";
            StatusMessage = FlakyTestSummary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Flaky test analysis failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadUseCaseTrends()
    {
        if (string.IsNullOrWhiteSpace(ResultsRootPath))
        {
            StatusMessage = "Set a results root path first.";
            return;
        }

        IsLoading = true;
        var maxBuilds = SelectedTrendRange switch
        {
            TrendRangeFilter.Last5 => 5,
            TrendRangeFilter.Last20 => 20,
            _ => 10,
        };
        StatusMessage = $"Building per-UseCase trends (last {maxBuilds} builds)...";

        try
        {
            var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
            var trends = await Task.Run(() =>
                analyzer.AnalyzePerUseCaseTrends(ResultsRootPath, _parser, maxBuilds: maxBuilds));

            UseCaseTrends.Clear();
            foreach (var (ucName, entries) in trends)
            {
                UseCaseTrends.Add(new UseCaseTrendViewModel
                {
                    UseCaseName = ucName,
                    Entries = entries,
                    LatestPassRate = entries.LastOrDefault()?.PassRate ?? 0,
                    Trend = CalculateTrend(entries),
                });
            }

            StatusMessage = $"Trends loaded for {UseCaseTrends.Count} use cases (last {maxBuilds} builds)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Trend analysis failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string CalculateTrend(List<UseCaseTrendEntry> entries)
    {
        if (entries.Count < 2) return "Stable";
        var recent = entries.TakeLast(3).Average(e => e.PassRate);
        var older = entries.Take(3).Average(e => e.PassRate);
        var diff = recent - older;
        if (diff > 5) return "Improving";
        if (diff < -5) return "Declining";
        return "Stable";
    }

    // ???????????????????????????????????????????????????????????????
    // Commands � Consolidated Reporting
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void SetScopeSingle() => CurrentScope = ReportScope.SingleBuild;

    [RelayCommand]
    private void SetScopeConsolidated() => CurrentScope = ReportScope.Consolidated;

    [RelayCommand]
    private void SetRange1D() => SelectedTimeRange = TimeRangeFilter.OneDay;

    [RelayCommand]
    private void SetRange1W() => SelectedTimeRange = TimeRangeFilter.OneWeek;

    [RelayCommand]
    private void SetRange1M() => SelectedTimeRange = TimeRangeFilter.OneMonth;

    [RelayCommand]
    private void SetTrendRange5() { SelectedTrendRange = TrendRangeFilter.Last5; _ = LoadUseCaseTrends(); }

    [RelayCommand]
    private void SetTrendRange10() { SelectedTrendRange = TrendRangeFilter.Last10; _ = LoadUseCaseTrends(); }

    [RelayCommand]
    private void SetTrendRange20() { SelectedTrendRange = TrendRangeFilter.Last20; _ = LoadUseCaseTrends(); }

    [RelayCommand]
    private async Task LoadConsolidatedBuilds()
    {
        RefreshBuilds();
        if (AvailableBuilds.Count == 0)
        {
            StatusMessage = "No builds found. Set the results root path first.";
            return;
        }

        var cutoff = GetTimeRangeCutoff();
        var filtered = AvailableBuilds.Where(b => b.ModifiedDate >= cutoff).ToList();
        if (filtered.Count == 0)
        {
            StatusMessage = "No builds match the selected time range.";
            return;
        }

        IsLoading = true;
        LoadedBuildNodes.Clear();
        _expandState.Clear();
        _expandState["AllBuilds"] = true;

        try
        {
            await ParseBuildsAsync(filtered);

            CurrentBuildNode = LoadedBuildNodes.FirstOrDefault();
            UpdateAggregateStats();
            SyncThresholdsToConfig();

            BuildMultiBuildResultsTree();
            BuildFlatList();
            UpdateAggregateHealth();
            await RunConsecutiveFailureDetection();

            var rangeLabel = GetRangeLabel(abbreviated: true);
            var aggRate = StatTotal > 0 ? (double)StatPassed / StatTotal * 100 : 0;
            ScopeSummaryText = $"Showing consolidated data for {filtered.Count} build(s) over the last {rangeLabel}.";
            StatusMessage = $"Consolidated: {LoadedBuildNodes.Count} builds ({StatTotal} tests, {aggRate:F1}% pass rate)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading consolidated builds: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task GenerateConsolidatedHtml()
    {
        if (IsConsolidatedMode)
        {
            if (LoadedBuildNodes.Count == 0)
                await LoadConsolidatedBuilds();
            if (LoadedBuildNodes.Count == 0) return;

            IsLoading = true;
            StatusMessage = "Generating consolidated HTML report...";
            try
            {
                var trend = await Task.Run(() =>
                {
                    var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
                    return analyzer.AnalyzeTrends(ResultsRootPath, _parser);
                });
                var html = _htmlGenerator.GenerateTrendHtml(trend);
                var path = Path.Combine(Path.GetTempPath(), "Consolidated_Report.html");
                File.WriteAllText(path, html);
                OpenInBrowser(path);
                StatusMessage = $"Consolidated report generated: {trend.Builds.Count} builds.";
            }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
            finally { IsLoading = false; }
        }
        else
        {
            ExportToHtmlCommand.Execute(null);
        }
    }
}
