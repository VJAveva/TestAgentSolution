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

    public ObservableCollection<BuildListItem> AvailableBuilds { get; } = new();
    public ObservableCollection<ResultsTreeNode> ResultsTree { get; } = new();

    /// <summary>Flat list of nodes for the ListView-based grid (proper column alignment).</summary>
    public ObservableCollection<ResultsFlatNode> FlatResultsList { get; } = new();

    /// <summary>All loaded builds (for multi-build / "Load All" support).</summary>
    public ObservableCollection<BuildNode> LoadedBuildNodes { get; } = new();

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

    // ?? Statistics for the ribbon/dashboard ??
    [ObservableProperty] private int _statTotal;
    [ObservableProperty] private int _statPassed;
    [ObservableProperty] private int _statFailed;
    [ObservableProperty] private int _statTimeout;

    // ?? Consecutive failure alerts ??
    [ObservableProperty] private ObservableCollection<ConsecutiveFailureAlert> _failureAlerts = new();
    [ObservableProperty] private string _alertStatusText = "";

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
    [ObservableProperty] private string _detailTrxFileName = "";
    [ObservableProperty] private string _detailTrxFilePath = "";
    [ObservableProperty] private bool _hasDetailSelected;

    // ?? Detail panel — execution steps ??
    [ObservableProperty] private ObservableCollection<StepRowVM> _detailExecutionSteps = new();
    [ObservableProperty] private bool _hasExecutionSteps;

    /// <summary>PassRateToColorConverter instance shared with the view.</summary>
    public PassRateToColorConverter PassRateConverter { get; }

    public BuildResultsViewModel(TrxResultsParser parser, BuildResultsAggregator aggregator, BuildResultsConfig config)
    {
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
        _goodThreshold = config.GoodThreshold;
        _warningThreshold = config.WarningThreshold;
        _resultsRootPath = config.ResultsRootPath;
        PassRateConverter = new PassRateToColorConverter(config);
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
            DetailTestName = tr.TestName;
            DetailUseCaseName = tr.UseCaseName;
            DetailBuildNumber = value.BuildNumber ?? "";
            DetailOutcome = tr.Outcome;
            DetailOutcomeColor = tr.Outcome == "Passed" ? "#10B981"
                : tr.Outcome == "Failed" ? "#EF4444" : "#F59E0B";
            DetailDuration = tr.Duration.ToString(@"hh\:mm\:ss\.fff");
            DetailErrorMessage = tr.ErrorMessage ?? "(no error message)";
            DetailStackTrace = tr.StackTrace ?? "(no stack trace)";
            DetailStdOut = tr.StdOut ?? "(no stdout captured)";
            DetailTrxFileName = tr.TrxFileName;

            // Fix: search with wildcard and AllDirectories for the TRX file
            var buildPath = LoadedBuildNodes
                .FirstOrDefault(b => b.BuildNumber == value.BuildNumber)?.RootPath;
            if (buildPath != null)
            {
                try
                {
                    DetailTrxFilePath = Directory.GetFiles(buildPath,
                        $"*{tr.TrxFileName}*.trx", SearchOption.AllDirectories)
                        .FirstOrDefault() ?? "";
                }
                catch { DetailTrxFilePath = ""; }
            }
            else
            {
                DetailTrxFilePath = "";
            }

            // Populate execution steps
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
        else if (value.NodeLevel is "Build" or "UseCase" or "AllBuilds")
        {
            DetailTestName = value.Name;
            DetailUseCaseName = "";
            DetailBuildNumber = value.BuildNumber ?? "";
            DetailOutcome = $"{value.Passed}/{value.Total} passed";
            DetailOutcomeColor = GetRateColor(value.PassRate ?? 0);
            DetailDuration = "";
            DetailErrorMessage = value.Failed > 0
                ? $"{value.Failed} test(s) failed" : "All tests passed";
            DetailStackTrace = "";
            DetailStdOut = "";
            DetailTrxFileName = "";
            DetailTrxFilePath = "";
            DetailExecutionSteps.Clear();
            HasExecutionSteps = false;
        }
        else
        {
            HasDetailSelected = false;
        }
    }

    [RelayCommand]
    private void OpenTrxInExplorer()
    {
        if (!string.IsNullOrEmpty(DetailTrxFilePath) && File.Exists(DetailTrxFilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{DetailTrxFilePath}\"");
            return;
        }

        // Fallback: try to find the file by searching recursively
        if (!string.IsNullOrEmpty(DetailTrxFileName))
        {
            var buildPath = LoadedBuildNodes
                .FirstOrDefault(b => b.BuildNumber == DetailBuildNumber)?.RootPath;
            if (buildPath != null)
            {
                try
                {
                    var found = Directory.GetFiles(buildPath,
                        $"*{DetailTrxFileName}*.trx", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (found != null)
                    {
                        Process.Start("explorer.exe", $"/select,\"{found}\"");
                        return;
                    }
                }
                catch { /* ignore search errors */ }
            }
        }

        StatusMessage = string.IsNullOrEmpty(DetailTrxFileName)
            ? "No TRX file associated with this selection."
            : $"TRX file not found: {DetailTrxFileName}";
    }

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

            // Replace loaded builds with just this one
            LoadedBuildNodes.Clear();
            LoadedBuildNodes.Add(buildNode);

            // Update statistics
            StatTotal = buildNode.TotalTests;
            StatPassed = buildNode.PassedTests;
            StatFailed = buildNode.FailedTests;
            StatTimeout = buildNode.TimeoutTests;

            if (buildNode.UseCases.Count == 0)
            {
                StatusMessage = $"No .trx files found in {SelectedBuild.Path}";
                ResultsTree.Clear();
                FlatResultsList.Clear();
                UpdateHealthIndicator(null);
                return;
            }

            _config.GoodThreshold = GoodThreshold;
            _config.WarningThreshold = WarningThreshold;

            // Build hierarchical tree (legacy)
            BuildResultsTree(buildNode);
            // Build flat list for ListView
            BuildFlatList();

            // Run consecutive failure detection
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
        if (AvailableBuilds.Count == 0)
        {
            RefreshBuilds();
            if (AvailableBuilds.Count == 0)
            {
                StatusMessage = "No builds found. Set the results root path first.";
                return;
            }
        }

        IsLoading = true;
        LoadedBuildNodes.Clear();

        try
        {
            foreach (var build in AvailableBuilds)
            {
                StatusMessage = $"Parsing {build.BuildNumber}...";
                var node = await Task.Run(() => _parser.ParseBuildFolder(build.Path));
                node = _aggregator.EvaluateBuildHealth(node);
                LoadedBuildNodes.Add(node);
                build.HasBeenLoaded = true;
            }

            // Set current to first
            CurrentBuildNode = LoadedBuildNodes.FirstOrDefault();

            // Update aggregate statistics
            StatTotal = LoadedBuildNodes.Sum(b => b.TotalTests);
            StatPassed = LoadedBuildNodes.Sum(b => b.PassedTests);
            StatFailed = LoadedBuildNodes.Sum(b => b.FailedTests);
            StatTimeout = LoadedBuildNodes.Sum(b => b.TimeoutTests);

            _config.GoodThreshold = GoodThreshold;
            _config.WarningThreshold = WarningThreshold;

            // Build trees
            BuildMultiBuildResultsTree();
            BuildFlatList();

            // Aggregate health
            var aggRate = StatTotal > 0 ? (double)StatPassed / StatTotal * 100 : 0;
            PassRatePercent = aggRate;
            var health = _aggregator.EvaluateHealth(aggRate);
            HealthColor = health switch { HealthStatus.Good => "#10B981", HealthStatus.Warning => "#F59E0B", _ => "#EF4444" };
            HealthLabel = health switch { HealthStatus.Good => "GOOD", HealthStatus.Warning => "WARNING", _ => "BAD" };

            // Run failure detection
            await RunConsecutiveFailureDetection();

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

    private void BuildResultsTree(BuildNode buildNode)
    {
        ResultsTree.Clear();

        var buildTreeNode = new ResultsTreeNode
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
            IsExpanded = true,
            ShowExportButtons = true,
        };

        // Wire up export commands for the build-level node
        buildTreeNode.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(buildNode));
        buildTreeNode.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(buildNode));

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

            buildTreeNode.Children.Add(ucNode);
        }

        ResultsTree.Add(buildTreeNode);
    }

    private void BuildMultiBuildResultsTree()
    {
        ResultsTree.Clear();

        foreach (var buildNode in LoadedBuildNodes)
        {
            var buildTreeNode = new ResultsTreeNode
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
                IsExpanded = false,
                ShowExportButtons = true,
            };

            buildTreeNode.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(buildNode));
            buildTreeNode.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(buildNode));

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

                buildTreeNode.Children.Add(ucNode);
            }

            ResultsTree.Add(buildTreeNode);
        }
    }

    /// <summary>Build a flat list for the ListView grid with proper column alignment.</summary>
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
                Total = allTotal,
                Passed = allPassed,
                Failed = allFailed,
                NotExecuted = allNotExe,
                PassRate = allRate,
                PassRateColor = GetRateColor(allRate),
                IndentLevel = 0,
                IsExpanded = true,
                ShowEmailButton = true,
                ShowStats = true,
            };
            allNode.SendEmailCommand = new RelayCommand(() => SendReportForAllBuilds());
            FlatResultsList.Add(allNode);
        }

        foreach (var buildNode in LoadedBuildNodes)
        {
            var buildIndent = hasAllBuildsRow ? 1 : 0;
            var buildRow = new ResultsFlatNode
            {
                Name = buildNode.BuildNumber,
                NodeLevel = "Build",
                BuildNumber = buildNode.BuildNumber,
                Total = buildNode.TotalTests,
                Passed = buildNode.PassedTests,
                Failed = buildNode.FailedTests,
                NotExecuted = buildNode.NotExecutedTests,
                PassRate = buildNode.PassRate,
                PassRateColor = GetRateColor(buildNode.PassRate),
                IndentLevel = buildIndent,
                IsExpanded = LoadedBuildNodes.Count == 1,
                ShowEmailButton = true,
                ShowStats = true,
            };
            var capturedNode = buildNode;
            buildRow.SendEmailCommand = new RelayCommand(() => SendReportForBuild(capturedNode));
            buildRow.ExportHtmlCommand = new RelayCommand(() => ExportBuildToHtml(capturedNode));
            buildRow.ExportCsvCommand = new RelayCommand(() => ExportBuildToCsv(capturedNode));
            buildRow.ToggleExpandCommand = new RelayCommand(() =>
            {
                buildRow.IsExpanded = !buildRow.IsExpanded;
                BuildFlatList();
            });
            FlatResultsList.Add(buildRow);

            if (buildRow.IsExpanded)
            {
                foreach (var uc in buildNode.UseCases)
                {
                    var ucRow = new ResultsFlatNode
                    {
                        Name = uc.UseCaseName,
                        NodeLevel = "UseCase",
                        BuildNumber = buildNode.BuildNumber,
                        Total = uc.Total,
                        Passed = uc.Passed,
                        Failed = uc.Failed,
                        NotExecuted = uc.NotExecuted,
                        PassRate = uc.PassRate,
                        PassRateColor = GetRateColor(uc.PassRate),
                        IndentLevel = buildIndent + 1,
                        IsExpanded = false,
                        ShowStats = true,
                    };
                    ucRow.ToggleExpandCommand = new RelayCommand(() =>
                    {
                        ucRow.IsExpanded = !ucRow.IsExpanded;
                        BuildFlatList();
                    });
                    FlatResultsList.Add(ucRow);

                    if (ucRow.IsExpanded)
                    {
                        foreach (var test in uc.TestResults)
                        {
                            FlatResultsList.Add(new ResultsFlatNode
                            {
                                Name = test.TestName,
                                NodeLevel = "TestResult",
                                BuildNumber = buildNode.BuildNumber,
                                Outcome = test.Outcome,
                                IndentLevel = buildIndent + 2,
                                TestResultModel = test,
                                ErrorMessage = TruncateError(test.ErrorMessage),
                                FullError = test.ErrorMessage ?? "",
                                FullStackTrace = test.StackTrace ?? "",
                                ShowStats = false,
                            });
                        }
                    }
                }
            }
        }
    }

    [RelayCommand]
    private void ApplyThresholds()
    {
        _config.GoodThreshold = GoodThreshold;
        _config.WarningThreshold = WarningThreshold;
        PassRateConverter.UpdateThresholds(GoodThreshold, WarningThreshold);
        if (CurrentBuildNode is not null)
        {
            UpdateHealthIndicator(CurrentBuildNode);
            RefreshTreeColors(ResultsTree);
        }
        // Rebuild flat list to update colors
        if (LoadedBuildNodes.Count > 0)
            BuildFlatList();
        StatusMessage = $"Thresholds updated: Good > {GoodThreshold}%, Warning ? {WarningThreshold}%";
    }

    private void RefreshTreeColors(ObservableCollection<ResultsTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.PassRate.HasValue)
                node.PassRateColor = GetRateColor(node.PassRate.Value);
            RefreshTreeColors(node.Children);
        }
    }

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

        var csv = GenerateCsvContent(CurrentBuildNode);
        File.WriteAllText(dlg.FileName, csv);
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

        var html = GenerateHtmlReport(CurrentBuildNode);
        File.WriteAllText(dlg.FileName, html);
        StatusMessage = $"Exported HTML report to {dlg.FileName}";
    }

    private void ExportBuildToHtml(BuildNode node)
    {
        var html = GenerateHtmlReport(node);
        var path = Path.Combine(Path.GetTempPath(), $"{node.BuildNumber}_Report.html");
        File.WriteAllText(path, html);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusMessage = $"Opened HTML report for {node.BuildNumber}";
    }

    private void ExportBuildToCsv(BuildNode node)
    {
        var csv = GenerateCsvContent(node);
        var path = Path.Combine(Path.GetTempPath(), $"{node.BuildNumber}_Results.csv");
        File.WriteAllText(path, csv);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusMessage = $"Opened CSV for {node.BuildNumber}";
    }

    [RelayCommand]
    private void SendReport()
    {
        if (CurrentBuildNode is null) return;

        if (string.IsNullOrWhiteSpace(_config.ReportRecipients) || string.IsNullOrWhiteSpace(_config.FromAddress))
        {
            StatusMessage = "Configure ReportRecipients and FromAddress in appsettings.json BuildResults section.";
            return;
        }

        try
        {
            var html = GenerateHtmlReport(CurrentBuildNode);
            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, _config.ReportRecipients)
            {
                Subject = $"Build Results: {CurrentBuildNode.BuildNumber} — {CurrentBuildNode.PassRate:F1}% ({CurrentBuildNode.Health})",
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"Report sent to {_config.ReportRecipients}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send report: {ex.Message}";
        }
    }

    private void SendReportForBuild(BuildNode node)
    {
        if (string.IsNullOrWhiteSpace(_config.ReportRecipients) || string.IsNullOrWhiteSpace(_config.FromAddress))
        {
            StatusMessage = "Configure ReportRecipients and FromAddress in appsettings.json BuildResults section.";
            return;
        }

        try
        {
            var html = GenerateHtmlReport(node);
            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, _config.ReportRecipients)
            {
                Subject = $"Build Results: {node.BuildNumber} — {node.PassRate:F1}% ({node.Health})",
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"Report for {node.BuildNumber} sent to {_config.ReportRecipients}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send report for {node.BuildNumber}: {ex.Message}";
        }
    }

    private void SendReportForAllBuilds()
    {
        if (string.IsNullOrWhiteSpace(_config.ReportRecipients) || string.IsNullOrWhiteSpace(_config.FromAddress))
        {
            StatusMessage = "Configure ReportRecipients and FromAddress in appsettings.json BuildResults section.";
            return;
        }

        try
        {
            var html = GenerateMultiBuildHtmlReport();
            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, _config.ReportRecipients)
            {
                Subject = $"All Build Results: {LoadedBuildNodes.Count} builds — {StatPassed}/{StatTotal} passed",
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"Aggregate report sent to {_config.ReportRecipients}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send aggregate report: {ex.Message}";
        }
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

    // ?? Trend Reports ???????????????????????????????????????????????

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

            var html = GenerateTrendHtml(trend);
            var path = Path.Combine(Path.GetTempPath(), "TestResults_Trend.html");
            File.WriteAllText(path, html);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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

    // ?? Consecutive Failure Detection ???????????????????????????????

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

    [RelayCommand]
    private void SendQaAlert()
    {
        if (FailureAlerts.Count == 0) return;

        if (string.IsNullOrWhiteSpace(_config.QaAlertRecipients) || string.IsNullOrWhiteSpace(_config.FromAddress))
        {
            StatusMessage = "Configure QaAlertRecipients and FromAddress in appsettings.json BuildResults section.";
            return;
        }

        try
        {
            var html = GenerateAlertEmailHtml();
            using var smtp = new SmtpClient(_config.SmtpServer, _config.SmtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(_config.FromAddress, _config.QaAlertRecipients)
            {
                Subject = $"\u26A0 Priority Investigation Required — {FailureAlerts.Count} tests failing consecutively",
                Body = html,
                IsBodyHtml = true,
            };
            smtp.Send(message);
            StatusMessage = $"QA alert sent to {_config.QaAlertRecipients}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send QA alert: {ex.Message}";
        }
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

    // ?? Helpers ??????????????????????????????????????????????????????

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
        switch (health)
        {
            case HealthStatus.Good:
                HealthColor = "#10B981";
                HealthLabel = "GOOD";
                break;
            case HealthStatus.Warning:
                HealthColor = "#F59E0B";
                HealthLabel = "WARNING";
                break;
            default:
                HealthColor = "#EF4444";
                HealthLabel = "BAD";
                break;
        }
    }

    private string GetRateColor(double rate) =>
        rate > GoodThreshold ? "#10B981"
        : rate >= WarningThreshold ? "#F59E0B"
        : "#EF4444";

    private static string TruncateError(string? msg) =>
        msg is null ? "" : msg.Length > 120 ? msg[..120] + "…" : msg;

    private string GenerateCsvContent(BuildNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Build,{node.BuildNumber}");
        sb.AppendLine($"Total,{node.TotalTests}");
        sb.AppendLine($"Passed,{node.PassedTests}");
        sb.AppendLine($"Failed,{node.FailedTests}");
        sb.AppendLine($"Timeout,{node.TimeoutTests}");
        sb.AppendLine($"PassRate,{node.PassRate:F1}%");
        sb.AppendLine();
        sb.AppendLine("UseCase,Duration,Total,Passed,Failed,Timeout,PassRate,FailedTests");
        foreach (var uc in node.UseCases)
        {
            var failedNames = string.Join("; ", uc.FailedTests.Select(t => t.TestName));
            sb.AppendLine($"\"{uc.UseCaseName}\",\"{uc.Duration:hh\\:mm\\:ss}\",{uc.Total},{uc.Passed},{uc.Failed},{uc.Timeout},{uc.PassRate:F1}%,\"{failedNames}\"");
        }
        return sb.ToString();
    }

    private string GenerateHtmlReport(BuildNode node)
    {
        var healthBg = node.Health switch
        {
            HealthStatus.Good => "#10B981",
            HealthStatus.Warning => "#F59E0B",
            _ => "#EF4444",
        };

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; background: #0F1629; color: #E2E8F0; margin: 0; padding: 20px; }");
        sb.AppendLine(".card { background: #1A2238; border-radius: 8px; padding: 16px; margin: 8px 0; }");
        sb.AppendLine(".health-bar { border-radius: 6px; height: 36px; display: flex; align-items: center; padding: 0 16px; font-weight: bold; font-size: 16px; color: #fff; }");
        sb.AppendLine(".stats { display: flex; gap: 12px; margin: 12px 0; }");
        sb.AppendLine(".stat { background: #1A2238; border-radius: 8px; padding: 12px 20px; text-align: center; flex: 1; }");
        sb.AppendLine(".stat .num { font-size: 28px; font-weight: bold; }");
        sb.AppendLine(".stat .lbl { font-size: 11px; color: #94A3B8; margin-top: 4px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 8px 0; }");
        sb.AppendLine("th { background: #1E293B; color: #94A3B8; text-align: left; padding: 8px 12px; font-size: 11px; text-transform: uppercase; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #1E293B; font-size: 13px; }");
        sb.AppendLine("tr:hover { background: #1E293B; }");
        sb.AppendLine(".fail { color: #EF4444; font-weight: 600; }");
        sb.AppendLine(".pass { color: #10B981; }");
        sb.AppendLine(".warn { color: #F59E0B; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h2>Build Results: {node.BuildNumber}</h2>");
        sb.AppendLine($"<div class='health-bar' style='background:{healthBg}'>{node.PassRate:F1}% — {node.Health}</div>");

        sb.AppendLine("<div class='stats'>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#60A5FA'>{node.TotalTests}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#10B981'>{node.PassedTests}</div><div class='lbl'>Passed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#EF4444'>{node.FailedTests}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#F59E0B'>{node.TimeoutTests}</div><div class='lbl'>Timeout</div></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h3>Use Case Breakdown</h3>");
        sb.AppendLine("<table><tr><th>Use Case</th><th>Duration</th><th>Total</th><th>Passed</th><th>Failed</th><th>Timeout</th><th>Pass Rate</th><th>Failed Tests</th></tr>");
        foreach (var uc in node.UseCases)
        {
            var rateClass = uc.PassRate > GoodThreshold ? "pass" : uc.PassRate >= WarningThreshold ? "warn" : "fail";
            var failedNames = string.Join(", ", uc.FailedTests.Select(t => t.TestName));
            sb.AppendLine($"<tr><td>{uc.UseCaseName}</td><td>{uc.Duration:hh\\:mm\\:ss}</td><td>{uc.Total}</td><td class='pass'>{uc.Passed}</td><td class='{(uc.Failed > 0 ? "fail" : "")}'>{uc.Failed}</td><td>{uc.Timeout}</td><td class='{rateClass}'>{uc.PassRate:F1}%</td><td class='fail' style='font-size:11px'>{failedNames}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        if (node.AllFailedTests.Count > 0)
        {
            sb.AppendLine("<div class='card'><h3>Failed Tests Detail</h3>");
            sb.AppendLine("<table><tr><th>Test Name</th><th>TRX File</th><th>Error Message</th></tr>");
            foreach (var t in node.AllFailedTests)
            {
                var errMsg = System.Net.WebUtility.HtmlEncode(TruncateError(t.ErrorMessage));
                sb.AppendLine($"<tr><td class='fail'>{t.TestName}</td><td>{t.TrxFileName}</td><td style='font-size:11px'>{errMsg}</td></tr>");
            }
            sb.AppendLine("</table></div>");
        }

        sb.AppendLine($"<p style='font-size:11px;color:#64748B;margin-top:20px'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string GenerateMultiBuildHtmlReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; background: #0F1629; color: #E2E8F0; margin: 0; padding: 20px; }");
        sb.AppendLine(".card { background: #1A2238; border-radius: 8px; padding: 16px; margin: 12px 0; }");
        sb.AppendLine("h2 { color: #60A5FA; } h3 { color: #94A3B8; margin-top: 0; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 8px 0; }");
        sb.AppendLine("th { background: #1E293B; color: #94A3B8; text-align: left; padding: 8px 12px; font-size: 11px; text-transform: uppercase; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #1E293B; font-size: 13px; }");
        sb.AppendLine("tr:hover { background: #1E293B; }");
        sb.AppendLine(".pass { color: #10B981; } .fail { color: #EF4444; } .warn { color: #F59E0B; }");
        sb.AppendLine(".send-btn { display:inline-block; background:#3B82F6; color:#fff; padding:10px 24px; border-radius:6px; text-decoration:none; font-weight:bold; margin:12px 4px; }");
        sb.AppendLine(".send-btn:hover { background:#2563EB; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h2>All Builds Summary ({LoadedBuildNodes.Count} builds)</h2>");

        sb.AppendLine("<div class='stats'>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#60A5FA'>{StatTotal}</div><div class='lbl'>Total</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#10B981'>{StatPassed}</div><div class='lbl'>Passed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#EF4444'>{StatFailed}</div><div class='lbl'>Failed</div></div>");
        sb.AppendLine($"<div class='stat'><div class='num' style='color:#F59E0B'>{StatTimeout}</div><div class='lbl'>Timeout</div></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h3>Build Breakdown</h3>");
        sb.AppendLine("<table><tr><th>Build</th><th>Total</th><th>Passed</th><th>Failed</th><th>Pass Rate</th><th>Health</th></tr>");
        foreach (var b in LoadedBuildNodes)
        {
            var rateClass = b.PassRate > GoodThreshold ? "pass" : b.PassRate >= WarningThreshold ? "warn" : "fail";
            sb.AppendLine($"<tr><td><strong>{b.BuildNumber}</strong></td><td>{b.TotalTests}</td><td class='pass'>{b.PassedTests}</td><td class='{(b.FailedTests > 0 ? "fail" : "")}'>{b.FailedTests}</td><td class='{rateClass}'>{b.PassRate:F1}%</td><td class='{rateClass}'>{b.Health}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        sb.AppendLine($"<p style='font-size:11px;color:#64748B;margin-top:20px'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string GenerateTrendHtml(TrendReport trend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");

        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; background: #0F1629; color: #E2E8F0; margin: 0; padding: 20px; }");
        sb.AppendLine(".card { background: #1A2238; border-radius: 8px; padding: 16px; margin: 12px 0; }");
        sb.AppendLine("h2 { color: #60A5FA; } h3 { color: #94A3B8; margin-top: 0; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 8px 0; }");
        sb.AppendLine("th { background: #1E293B; color: #94A3B8; text-align: left; padding: 8px 12px; font-size: 11px; text-transform: uppercase; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #1E293B; font-size: 13px; }");
        sb.AppendLine("tr:hover { background: #1E293B; }");
        sb.AppendLine(".pass { color: #10B981; } .fail { color: #EF4444; } .warn { color: #F59E0B; }");
        sb.AppendLine(".send-btn { display:inline-block; background:#3B82F6; color:#fff; padding:10px 24px; border-radius:6px; text-decoration:none; font-weight:bold; margin:12px 4px; }");
        sb.AppendLine(".send-btn:hover { background:#2563EB; }");
        sb.AppendLine("svg text { font-family: 'Segoe UI', Arial, sans-serif; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h2>Build Trend Report</h2>");
        sb.AppendLine($"<p style='color:#64748B'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} &mdash; {trend.Builds.Count} builds analyzed</p>");

        // SVG Charts (no JavaScript needed — works with file:// protocol)
        sb.AppendLine("<div class='card'><h3>Pass Rate Trend</h3>");
        sb.AppendLine(GeneratePassRateTrendSvg(trend.Builds));
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h3>Pass / Fail / Timeout per Build</h3>");
        sb.AppendLine(GeneratePassFailBarSvg(trend.Builds));
        sb.AppendLine("</div>");

        var totalP = trend.Builds.Sum(b => b.PassedTests);
        var totalF = trend.Builds.Sum(b => b.FailedTests);
        var totalT = trend.Builds.Sum(b => b.TimeoutTests);
        sb.AppendLine("<div class='card' style='max-width:500px'><h3>Overall Distribution</h3>");
        sb.AppendLine(GeneratePieChartSvg(totalP, totalF, totalT));
        sb.AppendLine("</div>");

        // Weekly summary table with per-build drill-down
        if (trend.WeeklySummaries.Count > 0)
        {
            sb.AppendLine("<div class='card'><h3>Weekly Summary</h3>");
            sb.AppendLine("<table><tr><th>Week</th><th>Build</th><th>Total</th><th>Passed</th><th>Failed</th><th>Pass Rate</th></tr>");
            foreach (var w in trend.WeeklySummaries)
            {
                var cls = w.AvgPassRate > _config.GoodThreshold ? "pass" : w.AvgPassRate >= _config.WarningThreshold ? "warn" : "fail";
                sb.AppendLine($"<tr style='background:#1E293B'><td colspan='2'><b>{w.Period}</b> ({w.BuildCount} builds)</td><td>{w.TotalTests}</td><td class='pass'>{w.TotalPassed}</td><td class='fail'>{w.TotalFailed}</td><td class='{cls}'>{w.AvgPassRate:F1}%</td></tr>");

                // Per-build rows within this week
                var weekKey = w.Period;
                foreach (var build in trend.Builds.Where(b =>
                    $"{b.Date.Year}-W{System.Globalization.ISOWeek.GetWeekOfYear(b.Date):D2}" == weekKey))
                {
                    var rateClass = build.PassRate > GoodThreshold ? "pass" : build.PassRate >= WarningThreshold ? "warn" : "fail";
                    sb.AppendLine($"<tr><td></td><td style='padding-left:20px'>{build.BuildNumber}</td><td>{build.TotalTests}</td><td class='pass'>{build.PassedTests}</td><td class='{(build.FailedTests > 0 ? "fail" : "")}'>{build.FailedTests}</td><td class='{rateClass}'>{build.PassRate:F1}%</td></tr>");
                }
            }
            sb.AppendLine("</table></div>");
        }

        // Monthly summary table with per-build drill-down
        if (trend.MonthlySummaries.Count > 0)
        {
            sb.AppendLine("<div class='card'><h3>Monthly Summary</h3>");
            sb.AppendLine("<table><tr><th>Month</th><th>Build</th><th>Total</th><th>Passed</th><th>Failed</th><th>Pass Rate</th></tr>");
            foreach (var m in trend.MonthlySummaries)
            {
                var cls = m.AvgPassRate > _config.GoodThreshold ? "pass" : m.AvgPassRate >= _config.WarningThreshold ? "warn" : "fail";
                sb.AppendLine($"<tr style='background:#1E293B'><td colspan='2'><b>{m.Period}</b> ({m.BuildCount} builds)</td><td>{m.TotalTests}</td><td class='pass'>{m.TotalPassed}</td><td class='fail'>{m.TotalFailed}</td><td class='{cls}'>{m.AvgPassRate:F1}%</td></tr>");

                var monthKey = m.Period;
                foreach (var build in trend.Builds.Where(b => b.Date.ToString("yyyy-MM") == monthKey))
                {
                    var rateClass = build.PassRate > GoodThreshold ? "pass" : build.PassRate >= WarningThreshold ? "warn" : "fail";
                    sb.AppendLine($"<tr><td></td><td style='padding-left:20px'>{build.BuildNumber}</td><td>{build.TotalTests}</td><td class='pass'>{build.PassedTests}</td><td class='{(build.FailedTests > 0 ? "fail" : "")}'>{build.FailedTests}</td><td class='{rateClass}'>{build.PassRate:F1}%</td></tr>");
                }
            }
            sb.AppendLine("</table></div>");
        }

        // Send Report button
        var recipients = _config.ReportRecipients ?? "team@company.com";
        sb.AppendLine("<div style='margin:20px 0;text-align:center'>");
        sb.AppendLine($"<a class='send-btn' href='mailto:{System.Net.WebUtility.HtmlEncode(recipients)}?subject=Build Trend Report {DateTime.Now:yyyy-MM-dd}'>\u2709 Send to Team</a>");
        sb.AppendLine("</div>");

        sb.AppendLine($"<p style='font-size:11px;color:#64748B;margin-top:20px'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string GeneratePassRateTrendSvg(List<BuildTrendEntry> builds)
    {
        if (builds.Count == 0) return "";

        int w = 800, h = 200, pad = 40;
        var chartW = w - pad * 2;
        var chartH = h - pad * 2;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox='0 0 {w} {h}' xmlns='http://www.w3.org/2000/svg' style='width:100%;max-height:220px'>");

        // Grid lines
        for (int i = 0; i <= 4; i++)
        {
            var y = pad + chartH * i / 4;
            var label = 100 - i * 25;
            sb.AppendLine($"<line x1='{pad}' y1='{y}' x2='{w - pad}' y2='{y}' stroke='#1E293B' stroke-width='1'/>");
            sb.AppendLine($"<text x='{pad - 5}' y='{y + 4}' fill='#94A3B8' font-size='10' text-anchor='end'>{label}%</text>");
        }

        // Data points and line
        var points = new List<string>();
        for (int i = 0; i < builds.Count; i++)
        {
            var x = pad + (builds.Count == 1 ? chartW / 2 : chartW * i / (builds.Count - 1));
            var yVal = pad + chartH * (1 - builds[i].PassRate / 100.0);
            points.Add($"{x:F0},{yVal:F0}");

            var color = builds[i].PassRate > GoodThreshold ? "#10B981"
                : builds[i].PassRate >= WarningThreshold ? "#F59E0B" : "#EF4444";

            sb.AppendLine($"<circle cx='{x:F0}' cy='{yVal:F0}' r='5' fill='{color}'/>");
            sb.AppendLine($"<text x='{x:F0}' y='{yVal - 10:F0}' fill='#E2E8F0' font-size='10' text-anchor='middle'>{builds[i].PassRate:F0}%</text>");

            var shortName = builds[i].BuildNumber.Length > 20
                ? builds[i].BuildNumber[^20..] : builds[i].BuildNumber;
            sb.AppendLine($"<text x='{x:F0}' y='{h - 5}' fill='#94A3B8' font-size='8' text-anchor='middle' transform='rotate(-30 {x:F0} {h - 5})'>{System.Net.WebUtility.HtmlEncode(shortName)}</text>");
        }

        if (points.Count > 1)
        {
            sb.AppendLine($"<polyline points='{string.Join(" ", points)}' fill='none' stroke='#3B82F6' stroke-width='2'/>");
            var areaPoints = string.Join(" ", points) + $" {w - pad},{pad + chartH} {pad},{pad + chartH}";
            sb.AppendLine($"<polygon points='{areaPoints}' fill='rgba(59,130,246,0.1)'/>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private string GeneratePassFailBarSvg(List<BuildTrendEntry> builds)
    {
        if (builds.Count == 0) return "";

        int w = 800, h = 200, pad = 40;
        var chartW = w - pad * 2;
        var chartH = h - pad * 2;
        var barW = Math.Max(chartW / Math.Max(builds.Count, 1) - 4, 8);
        var maxTotal = builds.Max(b => b.TotalTests);
        if (maxTotal == 0) maxTotal = 1;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox='0 0 {w} {h}' xmlns='http://www.w3.org/2000/svg' style='width:100%;max-height:220px'>");

        for (int i = 0; i < builds.Count; i++)
        {
            var x = pad + (chartW * i / Math.Max(builds.Count, 1)) + 2;
            var b = builds[i];

            var passH = (double)chartH * b.PassedTests / maxTotal;
            var failH = (double)chartH * b.FailedTests / maxTotal;
            var timeH = (double)chartH * b.TimeoutTests / maxTotal;

            var passY = pad + chartH - passH;
            var failY = passY - failH;
            var timeY = failY - timeH;

            sb.AppendLine($"<rect x='{x}' y='{passY:F0}' width='{barW}' height='{passH:F0}' fill='#10B981' rx='2'/>");
            if (failH > 0)
                sb.AppendLine($"<rect x='{x}' y='{failY:F0}' width='{barW}' height='{failH:F0}' fill='#EF4444' rx='2'/>");
            if (timeH > 0)
                sb.AppendLine($"<rect x='{x}' y='{timeY:F0}' width='{barW}' height='{timeH:F0}' fill='#F59E0B' rx='2'/>");
        }

        // Legend
        sb.AppendLine($"<rect x='{w - 160}' y='5' width='10' height='10' fill='#10B981' rx='2'/>");
        sb.AppendLine($"<text x='{w - 145}' y='14' fill='#E2E8F0' font-size='10'>Passed</text>");
        sb.AppendLine($"<rect x='{w - 100}' y='5' width='10' height='10' fill='#EF4444' rx='2'/>");
        sb.AppendLine($"<text x='{w - 85}' y='14' fill='#E2E8F0' font-size='10'>Failed</text>");
        sb.AppendLine($"<rect x='{w - 40}' y='5' width='10' height='10' fill='#F59E0B' rx='2'/>");
        sb.AppendLine($"<text x='{w - 25}' y='14' fill='#E2E8F0' font-size='10'>Timeout</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string GeneratePieChartSvg(int passed, int failed, int timeout)
    {
        var total = passed + failed + timeout;
        if (total == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("<svg viewBox='0 0 300 200' xmlns='http://www.w3.org/2000/svg' style='width:300px;height:200px'>");

        double cx = 100, cy = 100, r = 80;
        var slices = new[] {
            (Count: passed, Color: "#10B981", Label: "Passed"),
            (Count: failed, Color: "#EF4444", Label: "Failed"),
            (Count: timeout, Color: "#F59E0B", Label: "Timeout")
        }.Where(s => s.Count > 0).ToArray();

        double startAngle = -90; // Start from top
        foreach (var (count, color, label) in slices)
        {
            var pct = (double)count / total;
            var endAngle = startAngle + pct * 360;
            var large = pct > 0.5 ? 1 : 0;

            var x1 = cx + r * Math.Cos(startAngle * Math.PI / 180);
            var y1 = cy + r * Math.Sin(startAngle * Math.PI / 180);
            var x2 = cx + r * Math.Cos(endAngle * Math.PI / 180);
            var y2 = cy + r * Math.Sin(endAngle * Math.PI / 180);

            if (slices.Length == 1)
                sb.AppendLine($"<circle cx='{cx}' cy='{cy}' r='{r}' fill='{color}'/>");
            else
                sb.AppendLine($"<path d='M{cx},{cy} L{x1:F1},{y1:F1} A{r},{r} 0 {large},1 {x2:F1},{y2:F1} Z' fill='{color}'/>");

            startAngle = endAngle;
        }

        // Legend
        int ly = 30;
        foreach (var (count, color, label) in slices)
        {
            sb.AppendLine($"<rect x='210' y='{ly}' width='12' height='12' fill='{color}' rx='2'/>");
            sb.AppendLine($"<text x='228' y='{ly + 10}' fill='#E2E8F0' font-size='11'>{label}: {count} ({(double)count / total * 100:F1}%)</text>");
            ly += 22;
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private string GenerateAlertEmailHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; background: #fff; color: #1a1a2e; padding: 20px; }");
        sb.AppendLine("h2 { color: #EF4444; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 12px 0; }");
        sb.AppendLine("th { background: #f1f5f9; padding: 8px 12px; font-size: 11px; text-transform: uppercase; text-align: left; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #e2e8f0; fontSize: 13px; }");
        sb.AppendLine(".critical { color: #dc2626; font-weight: bold; }");
        sb.AppendLine(".high { color: #ea580c; font-weight: bold; }");
        sb.AppendLine(".medium { color: #d97706; font-weight: bold; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h2>\u26A0 Priority Investigation Required</h2>");
        sb.AppendLine($"<p>{FailureAlerts.Count} test(s) are failing across consecutive builds and require investigation.</p>");
        sb.AppendLine("<table><tr><th>Priority</th><th>Test Name</th><th>Use Case</th><th>Consecutive Fails</th><th>Failed In Builds</th><th>Last Error</th></tr>");
        foreach (var a in FailureAlerts)
        {
            var cls = a.Priority.ToLowerInvariant();
            var builds = string.Join(", ", a.FailedInBuilds);
            var err = System.Net.WebUtility.HtmlEncode(TruncateError(a.LastError));
            sb.AppendLine($"<tr><td class='{cls}'>{a.Priority}</td><td>{a.TestName}</td><td>{a.UseCaseName}</td><td>{a.ConsecutiveFailCount}</td><td style='font-size:11px'>{builds}</td><td style='font-size:11px'>{err}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine($"<p style='font-size:11px;color:#94a3b8'>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by TestController</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}

// ?? Supporting view models ??????????????????????????????????????????

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
