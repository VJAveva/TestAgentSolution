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
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;
    private readonly BuildReportHtmlGenerator _htmlGenerator;
    private readonly FailurePatternAnalyzer? _patternAnalyzer;
    private readonly CapabilityChecker? _capabilityChecker;

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

    public BuildResultsViewModel(TrxResultsParser parser, BuildResultsAggregator aggregator, BuildResultsConfig config, BuildReportHtmlGenerator htmlGenerator, FailurePatternAnalyzer? patternAnalyzer = null, CapabilityChecker? capabilityChecker = null)
    {
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
        _htmlGenerator = htmlGenerator;
        _patternAnalyzer = patternAnalyzer;
        _capabilityChecker = capabilityChecker;
        _goodThreshold = config.GoodThreshold;
        _warningThreshold = config.WarningThreshold;
        _resultsRootPath = config.ResultsRootPath;
        PassRateConverter = new PassRateToColorConverter(config);
        AnalyzeFailurePatternCommand = new RelayCommand<string>(OnAnalyzeFailurePattern, name => !string.IsNullOrWhiteSpace(name));

        // Initialize capability-gated flags
        RefreshReportCapabilities();
        if (_capabilityChecker is not null)
            _capabilityChecker.CapabilitiesChanged += OnCapabilitiesChanged;
    }

    /// <summary>
    /// True when the current user can export reports (Report_View — all roles).
    /// </summary>
    [ObservableProperty] private bool _canExportReport = true;

    /// <summary>
    /// True when the current user can send report emails (Report_Generate — Admin + SrMgr).
    /// </summary>
    [ObservableProperty] private bool _canSendReport = true;

    private void RefreshReportCapabilities()
    {
        CanExportReport = _capabilityChecker?.Can(TestControllerGrpc.Authorization.Permission.Report_View) ?? true;
        CanSendReport = _capabilityChecker?.Can(TestControllerGrpc.Authorization.Permission.Report_Generate) ?? true;
    }

    private void OnCapabilitiesChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(RefreshReportCapabilities);
    }

    /// <summary>
    /// Command bound to the "Analyze Failure Pattern" button on each
    /// consecutive-failure alert / failed test row. Parameter is the test name.
    /// </summary>
    public IRelayCommand<string> AnalyzeFailurePatternCommand { get; }

    private async void OnAnalyzeFailurePattern(string? testName)
        => await OnAnalyzeFailurePatternAsync(testName);

    private async Task OnAnalyzeFailurePatternAsync(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName)) return;
        if (_patternAnalyzer == null)
        {
            StatusMessage = "Failure analyzer is not available in this host.";
            return;
        }

        StatusMessage = $"Analyzing failure pattern for '{testName}'�";
        try
        {
            // Run TRX scan off the UI thread (network shares can take seconds).
            var report = await Task.Run(() =>
                _patternAnalyzer.AnalyzeTest(testName, lookbackBuilds: 5));

            var vm = new FailureAnalysisVM(report);
            var dialog = new TestControllerGrpc.Views.Dialogs.FailureAnalysisDialog(vm)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
            };
            StatusMessage = $"Analysis complete: {report.Pattern} ({report.Confidence}%).";
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failure analysis failed: {ex.Message}";
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(() =>
                        ThemedMessageBox.Show(
                            $"Failure analysis failed for '{testName}':\n\n{ex.Message}",
                            "Failure Pattern Analysis",
                            MessageBoxButton.OK, MessageBoxImage.Warning));
                }
            }
            catch
            {
                // Never let the error-reporting path tear down the process.
            }
        }
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
}
