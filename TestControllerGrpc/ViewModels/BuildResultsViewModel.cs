using System.Collections.ObjectModel;
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

namespace TestControllerGrpc.ViewModels;

public partial class BuildResultsViewModel : ObservableObject
{
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;
    private readonly BuildReportHtmlGenerator _htmlGenerator;

    public ObservableCollection<BuildListItem> AvailableBuilds { get; } = new();
    public ObservableCollection<ResultsTreeNode> ResultsTree { get; } = new();
    public ObservableCollection<ResultsFlatNode> FlatResultsList { get; } = new();
    public ObservableCollection<BuildNode> LoadedBuildNodes { get; } = new();

    private readonly Dictionary<string, bool> _expandState = new();
    private readonly ConcurrentDictionary<string, BuildNode> _buildCache = new();

    // ?? Core state ??
    [ObservableProperty] private BuildListItem? _selectedBuild;
    [ObservableProperty] private BuildNode? _currentBuildNode;
    [ObservableProperty] private string _healthColor = "#6B7280";
    [ObservableProperty] private string _healthLabel = "N/A";
    [ObservableProperty] private double _passRatePercent;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Select a build to view results.";
    [ObservableProperty] private double _goodThreshold;
    [ObservableProperty] private double _warningThreshold;
    [ObservableProperty] private string _resultsRootPath = "";

    // ?? Dashboard statistics ??
    [ObservableProperty] private int _statTotal;
    [ObservableProperty] private int _statPassed;
    [ObservableProperty] private int _statFailed;
    [ObservableProperty] private int _statTimeout;

    // ?? Consecutive failure alerts ??
    [ObservableProperty] private ObservableCollection<ConsecutiveFailureAlert> _failureAlerts = new();
    [ObservableProperty] private string _alertStatusText = "";

    // ?? Flaky test detection ??
    [ObservableProperty] private ObservableCollection<FlakyTestAlert> _flakyTests = new();
    [ObservableProperty] private string _flakyTestSummary = "";

    // ?? Per-UseCase trends ??
    [ObservableProperty] private ObservableCollection<UseCaseTrendViewModel> _useCaseTrends = new();

    // ?? Consolidated reporting ??
    [ObservableProperty] private ReportScope _currentScope = ReportScope.SingleBuild;
    [ObservableProperty] private TimeRangeFilter _selectedTimeRange = TimeRangeFilter.OneWeek;
    [ObservableProperty] private string _scopeSummaryText = "";
    [ObservableProperty] private int _filteredBuildCount;
    [ObservableProperty] private string _consolidatedFolderSummary = "";

    // ?? Trend range selector ??
    private TrendRangeFilter _selectedTrendRange = TrendRangeFilter.Last10;
    public TrendRangeFilter SelectedTrendRange
    {
        get => _selectedTrendRange;
        set => SetProperty(ref _selectedTrendRange, value);
    }

    public bool IsSingleBuildMode => CurrentScope == ReportScope.SingleBuild;
    public bool IsConsolidatedMode => CurrentScope == ReportScope.Consolidated;

    // ?? Detail panel ??
    [ObservableProperty] private ResultsFlatNode? _selectedResultNode;
    [ObservableProperty] private string _detailTestName = "";
    [ObservableProperty] private string _detailUseCaseName = "";
    [ObservableProperty] private string _detailBuildNumber = "";
    [ObservableProperty] private string _detailOutcome = "";
    [ObservableProperty] private string _detailOutcomeColor = "#6B7280";
    [ObservableProperty] private string _detailDuration = "";
    [ObservableProperty] private string _detailErrorMessage = "";
    [ObservableProperty] private string _detailStackTrace = "";
    [ObservableProperty] private string _detailStdOut = "";
    [ObservableProperty] private string _detailDebugTrace = "";
    [ObservableProperty] private string _detailTrxFileName = "";
    [ObservableProperty] private string _detailTrxFilePath = "";
    [ObservableProperty] private bool _hasDetailSelected;
    [ObservableProperty] private ObservableCollection<StepRowVM> _detailExecutionSteps = new();
    [ObservableProperty] private bool _hasExecutionSteps;

    public PassRateToColorConverter PassRateConverter { get; }

    // ???????????????????????????????????????????????????????????????
    // Constructor
    // ???????????????????????????????????????????????????????????????

    public BuildResultsViewModel(TrxResultsParser parser, BuildResultsAggregator aggregator, BuildResultsConfig config, BuildReportHtmlGenerator htmlGenerator)
    {
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
        _htmlGenerator = htmlGenerator;
        _goodThreshold = config.GoodThreshold;
        _warningThreshold = config.WarningThreshold;
        _resultsRootPath = config.ResultsRootPath;
        PassRateConverter = new PassRateToColorConverter(config);
    }

    // ???????????????????????????????????????????????????????????????
    // Property-change handlers
    // ???????????????????????????????????????????????????????????????

    partial void OnCurrentScopeChanged(ReportScope value)
    {
        OnPropertyChanged(nameof(IsSingleBuildMode));
        OnPropertyChanged(nameof(IsConsolidatedMode));
        if (value == ReportScope.Consolidated)
        {
            RefreshBuilds();
            ApplyTimeRangeFilter();
        }
        else
        {
            ScopeSummaryText = "";
            FilteredBuildCount = 0;
            ConsolidatedFolderSummary = "";
        }
    }

    partial void OnSelectedTimeRangeChanged(TimeRangeFilter value)
    {
        if (IsConsolidatedMode)
            ApplyTimeRangeFilter();
    }

    partial void OnSelectedBuildChanged(BuildListItem? value)
    {
        if (value is not null)
            LoadBuildResultsCommand.Execute(null);
    }

    partial void OnSelectedResultNodeChanged(ResultsFlatNode? value)
    {
        if (value is null)
        {
            HasDetailSelected = false;
            return;
        }

        HasDetailSelected = true;

        if (value.NodeLevel == "TestResult" && value.TestResultModel is TestResult tr)
        {
            PopulateTestResultDetail(value, tr);
        }
        else if (value.NodeLevel is "Build" or "UseCase" or "AllBuilds")
        {
            PopulateAggregateDetail(value);
        }
        else
        {
            HasDetailSelected = false;
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Commands — Build loading
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
    // Commands — Export & Report
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ExportToCsv()
    {
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
            DetailErrorMessage, DetailStackTrace, DetailStdOut, DetailTrxFilePath,
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
    // Commands — Email
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void SendReport()
    {
        if (CurrentBuildNode is null) return;
        var html = _htmlGenerator.GenerateSingleBuildHtml(CurrentBuildNode);
        var subject = $"Build Results: {CurrentBuildNode.BuildNumber} — {CurrentBuildNode.PassRate:F1}% ({CurrentBuildNode.Health})";
        SendEmail(_config.ReportRecipients, subject, html, "Report");
    }

    [RelayCommand]
    private void SendQaAlert()
    {
        if (FailureAlerts.Count == 0) return;
        var html = _htmlGenerator.GenerateAlertEmailHtml(FailureAlerts);
        var subject = $"\u26A0 Priority Investigation Required — {FailureAlerts.Count} tests failing consecutively";
        SendEmail(_config.QaAlertRecipients, subject, html, "QA alert");
    }

    [RelayCommand]
    private async Task SendEmailSummary()
    {
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
    // Commands — Thresholds & Clipboard
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
    // Commands — Trend & Consecutive Failure Detection
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private async Task GenerateTrendReport()
    {
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
    // Commands — Consolidated Reporting
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

    // ???????????????????????????????????????????????????????????????
    // Private helpers — Tree building
    // ???????????????????????????????????????????????????????????????

    private void BuildResultsTree(BuildNode buildNode)
    {
        ResultsTree.Clear();
        var treeNode = CreateBuildTreeNode(buildNode, isExpanded: true);
        ResultsTree.Add(treeNode);
    }

    private void BuildMultiBuildResultsTree()
    {
        ResultsTree.Clear();
        foreach (var buildNode in LoadedBuildNodes)
            ResultsTree.Add(CreateBuildTreeNode(buildNode, isExpanded: false));
    }

    private ResultsTreeNode CreateBuildTreeNode(BuildNode buildNode, bool isExpanded)
    {
        var treeNode = new ResultsTreeNode
        {
            Name = buildNode.BuildNumber,
            Total = buildNode.TotalTests,
            Passed = buildNode.PassedTests,
            Failed = buildNode.FailedTests,
            NotExecuted = buildNode.NotExecutedTests,
            PassRate = buildNode.PassRate,
            PassRateColor = GetRateColor(buildNode.PassRate),
            NodeLevel = "Build",
            ModifiedDate = buildNode.LatestRun,
            IsExpanded = isExpanded,
            ShowExportButtons = true,
        };

        treeNode.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(buildNode));
        treeNode.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(buildNode));

        foreach (var uc in buildNode.UseCases)
        {
            var ucNode = new ResultsTreeNode
            {
                Name = uc.UseCaseName,
                Total = uc.Total,
                Passed = uc.Passed,
                Failed = uc.Failed,
                NotExecuted = uc.NotExecuted,
                PassRate = uc.PassRate,
                PassRateColor = GetRateColor(uc.PassRate),
                NodeLevel = "UseCase",
            };

            foreach (var tr in uc.FailedTests)
            {
                ucNode.Children.Add(new ResultsTreeNode
                {
                    Name = tr.TestName,
                    NodeLevel = "TestResult",
                    Outcome = tr.Outcome,
                    ErrorMessage = TruncateError(tr.ErrorMessage),
                    FullError = tr.ErrorMessage ?? "",
                    FullStackTrace = tr.StackTrace ?? "",
                });
            }

            treeNode.Children.Add(ucNode);
        }

        return treeNode;
    }

    private void BuildFlatList()
    {
        FlatResultsList.Clear();
        var hasAllBuildsRow = LoadedBuildNodes.Count > 1;

        if (hasAllBuildsRow)
        {
            var allTotal = LoadedBuildNodes.Sum(b => b.TotalTests);
            var allPassed = LoadedBuildNodes.Sum(b => b.PassedTests);
            var allFailed = LoadedBuildNodes.Sum(b => b.FailedTests);
            var allNotExe = LoadedBuildNodes.Sum(b => b.NotExecutedTests);
            var allRate = allTotal > 0 ? (double)allPassed / allTotal * 100 : 0;

            var allNode = new ResultsFlatNode
            {
                Name = $"All Builds ({LoadedBuildNodes.Count})",
                NodeLevel = "AllBuilds",
                Total = allTotal, Passed = allPassed, Failed = allFailed, NotExecuted = allNotExe,
                PassRate = allRate, PassRateColor = GetRateColor(allRate),
                IndentLevel = 0, IsExpanded = GetExpandState("AllBuilds", true),
                ShowEmailButton = true, ShowStats = true,
            };
            allNode.SendEmailCommand = new RelayCommand(SendReportForAllBuilds);
            allNode.ToggleExpandCommand = new RelayCommand(() =>
            {
                SetExpandState("AllBuilds", !GetExpandState("AllBuilds", true));
                BuildFlatList();
            });
            FlatResultsList.Add(allNode);

            if (!allNode.IsExpanded) return;
        }

        foreach (var buildNode in LoadedBuildNodes)
        {
            var buildIndent = hasAllBuildsRow ? 1 : 0;
            var buildKey = $"Build|{buildNode.BuildNumber}";
            var buildExpanded = GetExpandState(buildKey, LoadedBuildNodes.Count == 1);

            var buildRow = new ResultsFlatNode
            {
                Name = buildNode.BuildNumber,
                NodeLevel = "Build", BuildNumber = buildNode.BuildNumber,
                Total = buildNode.TotalTests, Passed = buildNode.PassedTests,
                Failed = buildNode.FailedTests, NotExecuted = buildNode.NotExecutedTests,
                PassRate = buildNode.PassRate, PassRateColor = GetRateColor(buildNode.PassRate),
                IndentLevel = buildIndent, IsExpanded = buildExpanded,
                ShowEmailButton = true, ShowStats = true,
            };
            var capturedBuildKey = buildKey;
            var capturedNode = buildNode;
            buildRow.SendEmailCommand = new RelayCommand(() => SendReportForBuild(capturedNode));
            buildRow.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(capturedNode));
            buildRow.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(capturedNode));
            buildRow.ToggleExpandCommand = new RelayCommand(() =>
            {
                SetExpandState(capturedBuildKey, !GetExpandState(capturedBuildKey, false));
                BuildFlatList();
            });
            FlatResultsList.Add(buildRow);

            if (!buildExpanded) continue;

            foreach (var uc in buildNode.UseCases)
            {
                var ucKey = $"UC|{buildNode.BuildNumber}|{uc.UseCaseName}";
                var ucExpanded = GetExpandState(ucKey, false);

                var ucRow = new ResultsFlatNode
                {
                    Name = uc.UseCaseName, NodeLevel = "UseCase", BuildNumber = buildNode.BuildNumber,
                    Total = uc.Total, Passed = uc.Passed, Failed = uc.Failed, NotExecuted = uc.NotExecuted,
                    PassRate = uc.PassRate, PassRateColor = GetRateColor(uc.PassRate),
                    IndentLevel = buildIndent + 1, IsExpanded = ucExpanded, ShowStats = true,
                };
                var capturedUcKey = ucKey;
                ucRow.ToggleExpandCommand = new RelayCommand(() =>
                {
                    SetExpandState(capturedUcKey, !GetExpandState(capturedUcKey, false));
                    BuildFlatList();
                });
                FlatResultsList.Add(ucRow);

                if (!ucExpanded) continue;

                foreach (var test in uc.TestResults)
                {
                    FlatResultsList.Add(new ResultsFlatNode
                    {
                        Name = test.TestName, NodeLevel = "TestResult", BuildNumber = buildNode.BuildNumber,
                        Outcome = test.Outcome, IndentLevel = buildIndent + 2, TestResultModel = test,
                        ErrorMessage = TruncateError(test.ErrorMessage),
                        FullError = test.ErrorMessage ?? "", FullStackTrace = test.StackTrace ?? "",
                        ShowStats = false,
                    });
                }
            }
        }
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers — Detail panel population
    // ???????????????????????????????????????????????????????????????

    private void PopulateTestResultDetail(ResultsFlatNode node, TestResult tr)
    {
        DetailTestName = tr.TestName;
        DetailUseCaseName = tr.UseCaseName;
        DetailBuildNumber = node.BuildNumber ?? "";
        DetailOutcome = tr.Outcome;
        DetailOutcomeColor = tr.Outcome == "Passed" ? "#10B981"
            : tr.Outcome == "Failed" ? "#EF4444" : "#F59E0B";
        DetailDuration = tr.Duration.ToString(@"hh\:mm\:ss\.fff");
        DetailErrorMessage = tr.ErrorMessage ?? "(no error message)";
        DetailStackTrace = tr.StackTrace ?? "(no stack trace)";
        DetailStdOut = tr.StdOut ?? "(no stdout captured)";
        DetailDebugTrace = tr.DebugTrace ?? "(no debug trace)";
        DetailTrxFileName = tr.TrxFileName;

        var buildPath = LoadedBuildNodes
            .FirstOrDefault(b => b.BuildNumber == node.BuildNumber)?.RootPath;
        if (buildPath != null)
        {
            try
            {
                DetailTrxFilePath = Directory.GetFiles(buildPath,
                    $"*{tr.TrxFileName}*.trx", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }
            catch (IOException) { DetailTrxFilePath = ""; }
        }
        else
        {
            DetailTrxFilePath = "";
        }

        DetailExecutionSteps.Clear();
        if (tr.ExecutionSteps.Count > 0)
        {
            foreach (var step in tr.ExecutionSteps)
                DetailExecutionSteps.Add(new StepRowVM
                {
                    StepName = step.StepName,
                    Outcome = step.Outcome,
                    Duration = step.Duration,
                    OutcomeIcon = step.Outcome == "Passed" ? "\u2713" : step.Outcome == "Failed" ? "\u2717" : "\u25CB",
                    DurationText = step.Duration.TotalSeconds < 1
                        ? $"{step.Duration.TotalMilliseconds:F0}ms"
                        : step.Duration.ToString(@"mm\:ss"),
                });
            HasExecutionSteps = true;
        }
        else
        {
            HasExecutionSteps = false;
        }
    }

    private void PopulateAggregateDetail(ResultsFlatNode node)
    {
        DetailTestName = node.Name;
        DetailUseCaseName = "";
        DetailBuildNumber = node.BuildNumber ?? "";
        DetailOutcome = $"{node.Passed}/{node.Total} passed";
        DetailOutcomeColor = GetRateColor(node.PassRate ?? 0);
        DetailDuration = "";
        DetailErrorMessage = node.Failed > 0
            ? $"{node.Failed} test(s) failed" : "All tests passed";
        DetailStackTrace = "";
        DetailStdOut = "";
        DetailDebugTrace = "";
        DetailTrxFileName = "";
        DetailTrxFilePath = "";
        DetailExecutionSteps.Clear();
        HasExecutionSteps = false;
    }

    // ???????????????????????????????????????????????????????????????
    // Private helpers — Email (consolidated)
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
        var subject = $"Build Results: {node.BuildNumber} — {node.PassRate:F1}% ({node.Health})";
        SendEmail(_config.ReportRecipients, subject, html, $"Report for {node.BuildNumber}");
    }

    private void SendReportForAllBuilds()
    {
        var html = _htmlGenerator.GenerateMultiBuildHtml(
            LoadedBuildNodes.ToList(), StatTotal, StatPassed, StatFailed, StatTimeout);
        var subject = $"All Build Results: {LoadedBuildNodes.Count} builds — {StatPassed}/{StatTotal} passed";
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
    // Private helpers — Health & stats
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
        msg is null ? "" : msg.Length > 120 ? msg[..120] + "…" : msg;

    // ???????????????????????????????????????????????????????????????
    // Private helpers — Expand state & tree colors
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
    // Private helpers — Consecutive failure detection
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
    // Private helpers — Consolidated time range
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
            ? $"Folders in range ({rangeLabel}):\n" + string.Join("\n", matching.Select(b => $"  • {b.BuildNumber}  ({b.ModifiedDate:yyyy-MM-dd HH:mm})"))
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
    // Private helpers — Shared build parsing & browser launch
    // ???????????????????????????????????????????????????????????????

    private async Task ParseBuildsAsync(IEnumerable<BuildListItem> builds)
    {
        var buildList = builds.ToList();
        var total = buildList.Count;
        var done = 0;

        var semaphore = new SemaphoreSlim(4);
        var tasks = buildList.Select(async build =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!Directory.Exists(build.Path))
                {
                    Interlocked.Increment(ref done);
                    Application.Current?.Dispatcher.Invoke(() =>
                        StatusMessage = $"Skipped {build.BuildNumber} (folder no longer exists).");
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

                var current = Interlocked.Increment(ref done);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    LoadedBuildNodes.Add(node);
                    build.HasBeenLoaded = true;
                    StatusMessage = $"Parsing builds: {current}/{total} ({node.BuildNumber}: {node.TotalTests} tests)";
                });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref done);
                Application.Current?.Dispatcher.Invoke(() =>
                    StatusMessage = $"Skipped {build.BuildNumber}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Sort by date after all loaded
        var sorted = LoadedBuildNodes.OrderByDescending(b => b.LatestRun).ToList();
        LoadedBuildNodes.Clear();
        foreach (var n in sorted)
            LoadedBuildNodes.Add(n);
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
