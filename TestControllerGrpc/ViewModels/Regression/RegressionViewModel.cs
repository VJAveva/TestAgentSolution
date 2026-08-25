using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Regression;

/// <summary>
/// Root view-model for the Regression tab (docs/AzureIntegration/RegressionTab-UI-Spec.md).
/// Backed by <see cref="IRegressionDataProvider"/> — currently <c>MockRegressionDataProvider</c>
/// pending real Azure DevOps ingest. Recalculation order per the spec's cross-cutting section:
/// scope (R1/R2) hits the provider; category toggles (R3) and quick/column filters (R7/R8) are
/// client-side only over the already-fetched rows.
///
/// R4 dispatch/assign are ADVISORY ONLY in this pass — no suite-id-to-WatchItem-tag mapping
/// exists yet (architecture doc §11 gap scoreboard), so DispatchCommand reports what *would*
/// run rather than calling ExecutionController.TriggerWatchItem.
/// </summary>
public sealed partial class RegressionViewModel : ObservableObject
{
    private readonly IRegressionDataProvider _provider;
    private readonly IRegressionSourceCatalog _catalog;
    private readonly IChurnReportBuilder _reportBuilder;
    private readonly IChurnSummarizer _summarizer;
    private readonly IRegressionReportMailer _mailer;
    private readonly IInteractiveAdoAuthenticator _auth;
    private readonly BuildResultsConfig _resultsConfig;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<string> _ignoredFilePatterns;
    private readonly string _defaultBranch;

    private IReadOnlyList<SubsystemRow> _allRows = [];

    // Bumped on each load so a superseded (older) load discards its results instead of overwriting a newer one.
    private int _loadGeneration;

    // Minimum time the busy indicator stays on screen so fast (cached/mock) loads still register visually.
    private const int MinBusyVisibleMs = 400;

    public RegressionViewModel(
        IRegressionDataProvider provider,
        IRegressionSourceCatalog catalog,
        IChurnReportBuilder reportBuilder,
        IChurnSummarizer summarizer,
        IRegressionReportMailer mailer,
        IInteractiveAdoAuthenticator auth,
        BuildResultsConfig resultsConfig,
        IOptions<AdoOptions> adoOptions,
        IAppLogger logger)
    {
        _provider = provider;
        _catalog = catalog;
        _reportBuilder = reportBuilder;
        _summarizer = summarizer;
        _mailer = mailer;
        _auth = auth;
        _resultsConfig = resultsConfig;
        _ignoredFilePatterns = adoOptions.Value.IgnoredFilePatterns;
        _defaultBranch = adoOptions.Value.DefaultBranch ?? "";
        _logger = logger;
        _to = DateTime.Today;
        _from = DateTime.Today;
        _recipients = resultsConfig.ReportRecipients;
        InitSource();
        RefreshAuthState();
        _ = InitializeAsync();
    }

    public ObservableCollection<SubsystemRowViewModel> Rows { get; } = new();

    /// <summary>Definition/component picker (first entry is the "(All components)" sentinel).</summary>
    public ObservableCollection<RegressionComponentRef> Components { get; } = new();

    private static readonly RegressionComponentRef AllComponents = new("(All components)", 0);

    /// <summary>Release-branch switcher for parallel dev (first entry is the "(all branches)" sentinel).</summary>
    public ObservableCollection<string> Branches { get; } = new();

    private const string AllBranches = "(all branches)";

    /// <summary>Latest build number across the loaded rows — shown under the ribbon "Build" scope button.</summary>
    [ObservableProperty] private string _latestBuildText = "";

    [ObservableProperty] private string? _selectedBranch;

    private bool _suppressBranchReload;

    partial void OnSelectedBranchChanged(string? value)
    {
        if (_suppressBranchReload)
            return;
        _ = LoadAsync();
    }

    private async Task RefreshBranchesAsync()
    {
        try
        {
            var live = await _catalog.GetBranchesAsync(CancellationToken.None);
            _suppressBranchReload = true;
            var keep = SelectedBranch;
            Branches.Clear();
            Branches.Add(AllBranches);
            foreach (var b in live)
                Branches.Add(b);
            // Keep an explicit user pick; otherwise preselect the configured default branch on first load.
            SelectedBranch = keep is not null && keep != AllBranches && Branches.Contains(keep)
                ? keep
                : Branches.FirstOrDefault(b => string.Equals(b, _defaultBranch, StringComparison.OrdinalIgnoreCase)) ?? AllBranches;
        }
        catch (Exception ex)
        {
            _logger.Warn("Regression", $"Branch list load failed: {ex.Message}");
        }
        finally
        {
            _suppressBranchReload = false;
        }
    }

    private void InitSource()
    {
        RefreshConnection();

        // Direct backing-field writes are intentional: the generated property setters would fire
        // OnSelectedComponentChanged/OnSelectedBranchChanged during construction (premature ApplyFilter/LoadAsync).
#pragma warning disable MVVMTK0034
        Components.Clear();
        Components.Add(AllComponents);
        foreach (var c in _catalog.GetComponents())
            Components.Add(c);
        _selectedComponent = AllComponents;

        Branches.Clear();
        Branches.Add(AllBranches);
        _selectedBranch = AllBranches;
#pragma warning restore MVVMTK0034
    }

    private void RefreshConnection()
    {
        var info = _catalog.GetConnectionInfo();
        ConnectionText = info.Enabled
            ? $"ADO ON · {info.Organization}/{info.OmiProject} · mode={info.Mode} · {info.CredentialSource}" +
              (info.CredentialConfigured ? "" : " · NO CREDENTIAL")
            : "ADO OFF · showing mock/empty data (set Ado:Enabled=true)";
        IsConnected = info.Enabled && info.CredentialConfigured;
    }

    private void UpdateLatestBuild()
    {
        var latest = _allRows
            .Where(r => !string.IsNullOrEmpty(r.BuildNumber))
            .OrderByDescending(r => r.BuildFinishedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        LatestBuildText = latest?.BuildNumber ?? "";
    }

    private async Task InitializeAsync()
    {
        // Interactive auth: silently restore a prior sign-in before the first load; if still not signed in,
        // wait for the user to sign in rather than firing a doomed live query.
        if (_auth.IsAvailable)
        {
            await _auth.RestoreAsync(CancellationToken.None);
            RefreshAuthState();
            if (!_auth.IsSignedIn)
            {
                StatusMessage = "Not signed in to Azure DevOps \u2014 click \u201cSign in\u201d to load live data.";
                return;
            }
        }
        await RefreshBranchesAsync();
        await LoadAsync();
    }

    private void RefreshAuthState()
    {
        AuthAvailable = _auth.IsAvailable;
        IsSignedIn = _auth.IsSignedIn;
        SignInButtonText = _auth.IsSignedIn ? "Account" : "Sign in";
        AuthStatusText = !_auth.IsAvailable
            ? ""
            : _auth.IsSignedIn ? $"\u00b7 {_auth.SignedInUser}" : "\u00b7 not signed in";        RefreshConnection();    }

    /// <summary>Called by the sign-in dialog host after a successful sign-in to refresh state and reload.</summary>
    public void OnSignedInReload()
    {
        RefreshAuthState();
        if (_auth.IsSignedIn)
            _ = ReloadAfterSignInAsync();
    }

    private async Task ReloadAfterSignInAsync()
    {
        await RefreshBranchesAsync();
        await LoadAsync();
    }

    [ObservableProperty] private RegressionComponentRef? _selectedComponent;
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private bool _isConnected;

    // Interactive Entra sign-in state (drives the Sign-in button + status in the connection bar).
    [ObservableProperty] private bool _authAvailable;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _authStatusText = "";
    [ObservableProperty] private string _signInButtonText = "Sign in";

    /// <summary>Recent builds of the selected component (build picker).</summary>
    public ObservableCollection<RegressionBuildRef> Builds { get; } = new();

    [ObservableProperty] private RegressionBuildRef? _selectedBuild;
    [ObservableProperty] private bool _isBuildPickerEnabled;

    partial void OnSelectedComponentChanged(RegressionComponentRef? value)
    {
        ApplyFilter();
        _ = LoadBuildsForComponentAsync(value);
    }

    private async Task LoadBuildsForComponentAsync(RegressionComponentRef? component)
    {
        Builds.Clear();
        SelectedBuild = null;
        // Build picker only applies to a single, specific component (not the "(All)" sentinel).
        if (component is not { DefinitionId: > 0 })
        {
            IsBuildPickerEnabled = false;
            return;
        }

        try
        {
            var builds = await _catalog.GetComponentBuildsAsync(component.DefinitionId, CancellationToken.None);
            foreach (var b in builds)
                Builds.Add(b);
            IsBuildPickerEnabled = Builds.Count > 0;
        }
        catch (Exception ex)
        {
            IsBuildPickerEnabled = false;
            StatusMessage = $"Could not load builds for {component.Name}: {ex.Message}";
            _logger.Warn("Regression", $"Failed to load builds for {component.Name}: {ex.Message}");
        }
    }

    partial void OnSelectedBuildChanged(RegressionBuildRef? value)
    {
        if (value is not null && SelectedComponent is { DefinitionId: > 0 } comp)
            _ = LoadPickedBuildAsync(comp, value);
    }

    private async Task LoadPickedBuildAsync(RegressionComponentRef component, RegressionBuildRef build)
    {
        try
        {
            StatusMessage = $"Loading {component.Name} build {build.BuildNumber}\u2026";
            var row = await _catalog.GetComponentBuildImpactAsync(component.DefinitionId, build.BuildId, CancellationToken.None);
            Rows.Clear();
            if (row is not null)
            {
                Rows.Add(new SubsystemRowViewModel(row, _summarizer) { RowNumber = 1, IsExpanded = true });
                StatusMessage = $"{component.Name} build {build.BuildNumber}: {row.Changes.Count} change(s).";
            }
            else
            {
                StatusMessage = $"{component.Name} build {build.BuildNumber}: no data.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
            _logger.Error("Regression", "Failed to load picked build.", ex);
        }
    }

    [ObservableProperty] private RegressionScopeKind _selectedScope = RegressionScopeKind.Build;
    [ObservableProperty] private DateTime _from;
    [ObservableProperty] private DateTime _to;
    [ObservableProperty] private bool _showRuntime = true;
    [ObservableProperty] private bool _showConfig = true;

    // Work-item-type filters (R7): when any is on, show only rows carrying a work item of a checked type.
    [ObservableProperty] private bool _filterBug;
    [ObservableProperty] private bool _filterStory;
    [ObservableProperty] private bool _filterIms;

    // On by default so the grid leads with human-made changes; toggle off to also show automated build-tool changes.
    [ObservableProperty] private bool _hideAutomatedChanges = true;

    // Off by default: pipeline/shared noise files (e.g. <RepoName>.yaml, configs) are hidden from Files Modified.
    [ObservableProperty] private bool _showAllFiles;

    partial void OnFilterBugChanged(bool value) => ApplyFilter();
    partial void OnFilterStoryChanged(bool value) => ApplyFilter();
    partial void OnFilterImsChanged(bool value) => ApplyFilter();
    partial void OnHideAutomatedChangesChanged(bool value) => ApplyFilter();
    partial void OnShowAllFilesChanged(bool value) => ApplyFilter();

    [ObservableProperty] private string _scopeLabel = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _subsystemCount;
    [ObservableProperty] private int _fileCount;

    [ObservableProperty] private int _runtimeCount;
    [ObservableProperty] private int _configCount;
    [ObservableProperty] private int _noSuiteCount;

    [ObservableProperty] private int _planRuntimeSubsystems;
    [ObservableProperty] private int _planRuntimeSuites;
    [ObservableProperty] private int _planRuntimeManual;
    [ObservableProperty] private int _planRuntimeGaps;
    [ObservableProperty] private double _planRuntimeMinutes;

    [ObservableProperty] private int _planConfigSubsystems;
    [ObservableProperty] private int _planConfigSuites;
    [ObservableProperty] private int _planConfigManual;
    [ObservableProperty] private int _planConfigGaps;
    [ObservableProperty] private double _planConfigMinutes;

    [ObservableProperty] private int _totalAutomatedSuites;
    [ObservableProperty] private int _totalManualSuites;
    [ObservableProperty] private int _totalGaps;
    [ObservableProperty] private double _parallelDurationMinutes;

    [ObservableProperty] private string _syncState = "";
    [ObservableProperty] private string _mapVersion = "";
    [ObservableProperty] private string _unresolvedRepositories = "";

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>True while a scope load is in flight — drives the ribbon busy indicator.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>One-line headline of the current scope (always shown in the compact AI Summary bar).</summary>
    [ObservableProperty] private string _aiSummaryHeadline = "";

    /// <summary>Full highlight bullets, shown only when the AI Summary pane is expanded.</summary>
    [ObservableProperty] private string _aiSummaryText = "";

    /// <summary>Collapsed by default so the summary pane doesn't steal grid space (autohide).</summary>
    [ObservableProperty] private bool _isSummaryExpanded;

    /// <summary>Comma/semicolon-separated report recipients (prefilled from BuildResults:ReportRecipients).</summary>
    [ObservableProperty] private string _recipients = "";

    partial void OnShowRuntimeChanged(bool value) => ApplyFilter();
    partial void OnShowConfigChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void SelectScope(string scopeName)
    {
        if (!Enum.TryParse<RegressionScopeKind>(scopeName, out var scope))
            return;

        SelectedScope = scope;
        var today = DateTime.Today;
        (From, To) = scope switch
        {
            RegressionScopeKind.Build => (today, today),
            RegressionScopeKind.Weekly => (today.AddDays(-7), today),
            RegressionScopeKind.Release => (today.AddDays(-45), today),
            _ => (From, To), // Custom: leave as-is, user drives via date pickers
        };
        _ = LoadAsync();
    }

    [RelayCommand]
    private void ApplyRange()
    {
        if (To < From)
        {
            StatusMessage = "'To' cannot be before 'From'.";
            return;
        }
        SelectedScope = RegressionScopeKind.Custom;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Latest-wins without forced cancellation: a newer load bumps the generation so an older,
        // superseded load discards its results instead of cancelling the in-flight ADO request.
        var myGeneration = ++_loadGeneration;

        // Immediate feedback so the ribbon click doesn't feel unresponsive during the ADO round-trip.
        IsBusy = true;
        StatusMessage = "Loading changes\u2026";
        var startedAtMs = Environment.TickCount64;
        try
        {
            // Build scope shows the current build's changes vs the previous build (latest-build mode, no date window).
            DateOnly? fromDate = SelectedScope == RegressionScopeKind.Build ? null : DateOnly.FromDateTime(From);
            DateOnly? toDate = SelectedScope == RegressionScopeKind.Build ? null : DateOnly.FromDateTime(To);
            var branch = SelectedBranch == AllBranches ? null : SelectedBranch;

            // Offload to a background thread so the UI thread is free to paint the busy indicator,
            // even when the provider completes synchronously (mock data or a warm cache).
            var consolidated = await Task.Run(() => _provider.GetConsolidatedAsync(fromDate, toDate, branch, CancellationToken.None));
            if (myGeneration != _loadGeneration) return;
            _allRows = consolidated.Rows;
            UpdateLatestBuild();

            ScopeLabel = SelectedScope.ToString();
            RangeText = consolidated.Summary.RangeText;
            ChangeCount = consolidated.Summary.ChangeCount;
            SubsystemCount = consolidated.Summary.SubsystemCount;
            FileCount = consolidated.Summary.FileCount;

            var scope = await _provider.GetScopeAsync(fromDate, toDate, category: null, branch, CancellationToken.None);
            if (myGeneration != _loadGeneration) return;
            PlanRuntimeSubsystems = scope.Runtime.Subsystems;
            PlanRuntimeSuites = scope.Runtime.AutomatedSuites;
            PlanRuntimeManual = scope.Runtime.ManualSuites;
            PlanRuntimeGaps = scope.Runtime.Gaps;
            PlanRuntimeMinutes = scope.Runtime.EstimatedMinutes;

            PlanConfigSubsystems = scope.Config.Subsystems;
            PlanConfigSuites = scope.Config.AutomatedSuites;
            PlanConfigManual = scope.Config.ManualSuites;
            PlanConfigGaps = scope.Config.Gaps;
            PlanConfigMinutes = scope.Config.EstimatedMinutes;

            TotalAutomatedSuites = scope.Runtime.AutomatedSuites + scope.Config.AutomatedSuites;
            TotalManualSuites = scope.Runtime.ManualSuites + scope.Config.ManualSuites;
            TotalGaps = scope.UnmappedSubsystems.Count;
            var totalMinutes = scope.Runtime.EstimatedMinutes + scope.Config.EstimatedMinutes;
            ParallelDurationMinutes = scope.ParallelAgentCount > 0 ? totalMinutes / scope.ParallelAgentCount : totalMinutes;

            var sync = await _provider.GetSyncStatusAsync(CancellationToken.None);
            if (myGeneration != _loadGeneration) return;
            SyncState = sync.State;
            MapVersion = sync.MapVersion;
            UnresolvedRepositories = sync.UnresolvedRepositories.Count == 0
                ? "none"
                : string.Join(", ", sync.UnresolvedRepositories);

            ApplyFilter();
            StatusMessage = $"Loaded {SubsystemCount} subsystem(s), {ChangeCount} change(s).";
            _logger.Info("Regression", $"Loaded scope {SelectedScope} [{RangeText}]: {SubsystemCount} subsystems.");
        }
        catch (Exception ex)
        {
            // Only the current load surfaces an error; a superseded load fails silently.
            if (myGeneration == _loadGeneration)
            {
                StatusMessage = ex is OperationCanceledException
                    ? "Load canceled or timed out \u2014 check the Azure DevOps connection."
                    : $"Load failed: {ex.Message}";
                _logger.Error("Regression", "Failed to load consolidated impact.", ex);
            }
        }
        finally
        {
            // Only the most recent load owns the busy indicator; a superseded load leaves it to the newer one.
            if (myGeneration == _loadGeneration)
            {
                // Keep the indicator visible a beat longer so very fast (cached/mock) loads still register.
                var remaining = MinBusyVisibleMs - (int)(Environment.TickCount64 - startedAtMs);
                if (remaining > 0)
                    await Task.Delay(remaining);

                if (myGeneration == _loadGeneration)
                    IsBusy = false;
            }
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allRows.Where(r =>
            r.Category == RegressionCategoryKind.Unclassified ||
            r.Category == RegressionCategoryKind.Both ||
            (r.Category == RegressionCategoryKind.Runtime && ShowRuntime) ||
            (r.Category == RegressionCategoryKind.Config && ShowConfig));

        // Definition/component picker: narrow to one component unless "(All components)" is selected.
        if (SelectedComponent is { DefinitionId: > 0 } sel)
            filtered = filtered.Where(r => string.Equals(r.Component, sel.Name, StringComparison.OrdinalIgnoreCase));

        // Human-only (default): strip automated build-tool changes and drop rows with no human change left.
        IEnumerable<SubsystemRow> projected = HideAutomatedChanges
            ? filtered.Select(StripAutomatedChanges).Where(r => r.Changes.Count > 0)
            : filtered;

        var list = projected.Select(r => new SubsystemRowViewModel(r, _summarizer, BuildFileFilter(r))).ToList();

        // Work-item-type filter (OR across the checked types); no filter when none are checked.
        if (FilterBug || FilterStory || FilterIms)
        {
            list = list.Where(vm =>
                (FilterBug && vm.HasBug) ||
                (FilterStory && vm.HasStory) ||
                (FilterIms && vm.HasIms)).ToList();
        }

        Rows.Clear();
        var n = 1;
        foreach (var vm in list)
        {
            vm.RowNumber = n++;
            Rows.Add(vm);
        }

        RuntimeCount = Rows.Count(r => r.Category is RegressionCategoryKind.Runtime or RegressionCategoryKind.Both);
        ConfigCount = Rows.Count(r => r.Category is RegressionCategoryKind.Config or RegressionCategoryKind.Both);
        NoSuiteCount = Rows.Count(r => r.AutomatedSuiteCount == 0 && r.ManualSuiteCount == 0);

        // Keep the header counters in sync with what's actually shown in the grid.
        SubsystemCount = Rows.Count;
        ChangeCount = Rows.Sum(r => r.Changes.Count);
        FileCount = Rows.Sum(r => r.TotalFilesModified);

        UpdateAiSummary();
    }

    /// <summary>Returns a copy of the row with automated build-tool changes removed (files/counts recomputed).</summary>
    private static SubsystemRow StripAutomatedChanges(SubsystemRow row)
    {
        var human = row.Changes.Where(c => c.Kind != RegressionChangeKind.Automated).ToList();
        if (human.Count == row.Changes.Count)
            return row;
        var files = human.SelectMany(c => c.FilePaths).Distinct().ToList();
        return row with
        {
            Changes = human,
            FilesModified = files.Take(25).ToList(),
            TotalFilesModified = files.Count,
        };
    }

    // Null when "All files" is on; otherwise hides pipeline/shared noise files from a row's file lists.
    private Func<string, bool>? BuildFileFilter(SubsystemRow row) =>
        ShowAllFiles ? null : p => !FileNoiseFilter.IsIgnored(p, row.Repository, _ignoredFilePatterns);

    private void UpdateAiSummary()
    {
        try
        {
            var summary = _summarizer.Summarize(BuildCurrentReport());
            AiSummaryHeadline = summary.Headline;
            AiSummaryText = summary.Highlights.Count == 0
                ? ""
                : string.Join("\n", summary.Highlights.Select(h => "\u2022 " + h));
        }
        catch (Exception ex)
        {
            AiSummaryHeadline = "";
            AiSummaryText = "";
            _logger.Warn("Regression", $"AI summary generation failed: {ex.Message}");
        }
    }

    /// <summary>Snapshot of the rows currently shown in the grid, for export / email / summary.</summary>
    private ChurnReport BuildCurrentReport() => new(
        ScopeLabel: SelectedScope.ToString(),
        RangeText: string.IsNullOrEmpty(RangeText)
            ? $"{DateOnly.FromDateTime(From):yyyy-MM-dd} \u2013 {DateOnly.FromDateTime(To):yyyy-MM-dd}"
            : RangeText,
        From: DateOnly.FromDateTime(From),
        To: DateOnly.FromDateTime(To),
        GeneratedUtc: DateTimeOffset.UtcNow,
        Rows: Rows.Select(r => r.Model).ToList());

    /// <summary>
    /// R4/R13 — advisory only. No suite-id \u2192 WatchItem-tag mapping exists yet, so this
    /// reports what would be dispatched rather than calling the real run endpoint.
    /// </summary>
    [RelayCommand]
    private void Dispatch()
    {
        StatusMessage = $"Advisory: would dispatch {TotalAutomatedSuites} automated suite(s) across {PlanRuntimeSubsystems + PlanConfigSubsystems} subsystem(s). " +
                        "Real dispatch is blocked on a suite-id\u2192WatchItem-tag mapping (not yet built).";
        _logger.Info("Regression", "Dispatch requested (advisory-only, no real trigger wired).");
    }

    [RelayCommand]
    private void AssignManual()
    {
        StatusMessage = $"Advisory: {TotalManualSuites} manual suite(s) would be assigned. No assignment workflow wired yet.";
        _logger.Info("Regression", "AssignManual requested (advisory-only).");
    }

    [RelayCommand]
    private void Export()
    {
        if (Rows.Count == 0)
        {
            StatusMessage = "Nothing to export \u2014 load a scope first.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export code churn report",
            FileName = $"churn-report-{DateOnly.FromDateTime(From):yyyyMMdd}-{DateOnly.FromDateTime(To):yyyyMMdd}",
            DefaultExt = ".html",
            Filter = "HTML report (*.html)|*.html|CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var report = BuildCurrentReport();
            var isCsv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            var content = isCsv ? _reportBuilder.BuildCsv(report) : _reportBuilder.BuildHtml(report);
            File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
            StatusMessage = $"Exported {Rows.Count} row(s) to {dlg.FileName}";
            _logger.Info("Regression", $"Churn report exported ({(isCsv ? "csv" : "html")}) to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            _logger.Error("Regression", "Churn report export failed.", ex);
        }
    }

    [RelayCommand]
    private async Task EmailReportAsync()
    {
        if (Rows.Count == 0)
        {
            StatusMessage = "Nothing to email \u2014 load a scope first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Recipients))
        {
            StatusMessage = "Enter at least one recipient email address.";
            return;
        }

        try
        {
            StatusMessage = $"Emailing report to {Recipients}\u2026";
            var report = BuildCurrentReport();
            var html = _reportBuilder.BuildHtml(report);
            var csv = _reportBuilder.BuildCsv(report);
            var csvName = $"churn-report-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv";
            var subject = $"Code churn report \u00b7 {report.RangeText} \u00b7 {report.Rows.Count} component(s)";
            var recipients = Recipients;
            await Task.Run(() => _mailer.Send(recipients, subject, html, csv, csvName));
            StatusMessage = $"Report emailed to {recipients}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Email failed: {ex.Message}";
            _logger.Error("Regression", "Failed to email churn report.", ex);
        }
    }
}
