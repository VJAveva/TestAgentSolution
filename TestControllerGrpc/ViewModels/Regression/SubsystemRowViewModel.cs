using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels.Regression;

/// <summary>Read-only projection of a <see cref="SubsystemRow"/> for the Regression grid (R9).</summary>
public sealed partial class SubsystemRowViewModel : ObservableObject
{
    private readonly IChurnSummarizer? _summarizer;
    private readonly Func<string, bool>? _keepFile;
    private readonly IRegressionImpactMatcher? _matcher;
    private string? _aiSummary;
    private bool _matchesLoaded;

    public SubsystemRowViewModel(
        SubsystemRow model, IChurnSummarizer? summarizer = null, Func<string, bool>? keepFile = null,
        IRegressionImpactMatcher? matcher = null)
    {
        Model = model;
        _summarizer = summarizer;
        _keepFile = keepFile;
        _matcher = matcher;
    }

    public SubsystemRow Model { get; }

    /// <summary>Component-scoped AI summary (computed lazily; shown in the expanded row detail).</summary>
    public string AiSummary => _aiSummary ??= _summarizer?.SummarizeComponent(Model) ?? "";

    public string Component => Model.Component;
    public string Subsystem => Model.Subsystem;
    public RegressionCategoryKind Category => Model.Category;
    public RegressionEvidenceKind CategoryConfidence => Model.CategoryConfidence;
    public string RiskTier => Model.RiskTier;
    public int TotalFilesModified => Model.TotalFilesModified;

    /// <summary>Modified files after the "All files" toggle filter (null filter = show everything).</summary>
    private IReadOnlyList<string> DisplayFiles =>
        _keepFile is null ? Model.FilesModified : Model.FilesModified.Where(_keepFile).ToList();

    public string FilesPreview => string.Join(", ", DisplayFiles.Take(2)) +
        (DisplayFiles.Count > 2 ? $" (+{DisplayFiles.Count - 2} more)" : "");

    /// <summary>Subsystems = the .sln files found under /src of the component's repository.</summary>
    public IReadOnlyList<string> Subsystems => Model.SolutionNames ?? [];
    public string SubsystemsText => Subsystems.Count == 0 ? "\u2014" : string.Join(", ", Subsystems);

    /// <summary>Modified files as ADO web links — pinned to the commit that touched them, else the default branch.</summary>
    public IReadOnlyList<ModifiedFileLink> ModifiedFiles
    {
        get
        {
            var byPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // path -> commit id
            foreach (var ch in Model.Changes)
                foreach (var p in ch.FilePaths)
                    if (!string.IsNullOrWhiteSpace(p) && (_keepFile is null || _keepFile(p)) && !byPath.ContainsKey(p))
                        byPath[p] = ch.ChangeId;
            foreach (var p in Model.FilesModified)
                if (!string.IsNullOrWhiteSpace(p) && (_keepFile is null || _keepFile(p)) && !byPath.ContainsKey(p))
                    byPath[p] = null;
            return byPath
                .Take(50)
                .Select(kv => new ModifiedFileLink(kv.Key, FileUrl(kv.Key, kv.Value)))
                .ToList();
        }
    }

    public bool HasModifiedFiles => ModifiedFiles.Count > 0;

    private string? FileUrl(string path, string? commitId)
    {
        if (string.IsNullOrWhiteSpace(Model.RepositoryUrl))
            return null;
        var url = $"{Model.RepositoryUrl}?path={Uri.EscapeDataString(path)}";
        if (!string.IsNullOrWhiteSpace(commitId))
            url += $"&version=GC{commitId}";
        else if (!string.IsNullOrWhiteSpace(Model.DefaultBranch))
            url += $"&version=GB{Uri.EscapeDataString(Model.DefaultBranch)}";
        return url;
    }

    public IReadOnlyList<RegressionChangeRef> Changes => Model.Changes;

    /// <summary>Changes ordered oldest→newest so the PR/commit sequence reads top-to-bottom.</summary>
    public IReadOnlyList<RegressionChangeRef> SortedChanges =>
        Model.Changes.OrderBy(c => c.ObservedUtc).ToList();

    public string SummaryPreview => Model.Changes.Count == 0
        ? ""
        : Model.Changes[0].Summary + (Model.Changes.Count > 1 ? $" (+{Model.Changes.Count - 1} more)" : "");

    public int PullRequestCount => Model.Changes.Count(c => c.Kind == RegressionChangeKind.PullRequest);
    public int AutomatedChangeCount => Model.Changes.Count(c => c.Kind == RegressionChangeKind.Automated);
    public int CommitChangeCount => Model.Changes.Count(c => c.Kind == RegressionChangeKind.Commit);

    /// <summary>True when the component changed but every change is an automated build-syncup (no PR/human commit).</summary>
    public bool IsAutomatedOnly => Model.Changes.Count > 0 && PullRequestCount == 0 && CommitChangeCount == 0;

    /// <summary>Compact label of change composition, e.g. "2 PR · 3 auto · 1 commit".</summary>
    public string ChangeKindLabel
    {
        get
        {
            var parts = new List<string>(3);
            if (PullRequestCount > 0) parts.Add($"{PullRequestCount} PR");
            if (CommitChangeCount > 0) parts.Add($"{CommitChangeCount} commit");
            if (AutomatedChangeCount > 0) parts.Add($"{AutomatedChangeCount} auto");
            return string.Join(" \u00b7 ", parts);
        }
    }

    public IReadOnlyList<RegressionSuiteRef> AutomatedSuites => Model.AutomatedSuites;
    public IReadOnlyList<RegressionSuiteRef> ManualSuites => Model.ManualSuites;
    public int AutomatedSuiteCount => Model.AutomatedSuites.Count;
    public int ManualSuiteCount => Model.ManualSuites.Count;
    public bool HasNoSuite => AutomatedSuiteCount == 0 && ManualSuiteCount == 0;

    /// <summary>How many manual test cases render inline before the rest move behind the overflow chip.</summary>
    private const int VisibleManualSuiteCap = 11;

    private IReadOnlyList<ManualSuiteLink>? _manualSuiteLinks;

    /// <summary>Manual test cases as titled Azure DevOps links — a bare work-item id is not a test case name.</summary>
    public IReadOnlyList<ManualSuiteLink> ManualSuiteLinks =>
        _manualSuiteLinks ??= Model.ManualSuites.Select(ToManualSuiteLink).ToList();

    public IReadOnlyList<ManualSuiteLink> VisibleManualSuites =>
        ManualSuiteLinks.Take(VisibleManualSuiteCap).ToList();

    public IReadOnlyList<ManualSuiteLink> OverflowManualSuites =>
        ManualSuiteLinks.Skip(VisibleManualSuiteCap).ToList();

    public bool HasManualSuites => ManualSuiteLinks.Count > 0;
    public bool HasOverflowManualSuites => ManualSuiteLinks.Count > VisibleManualSuiteCap;
    public string OverflowManualSuiteLabel => $"+{ManualSuiteLinks.Count - VisibleManualSuiteCap} more";

    private static ManualSuiteLink ToManualSuiteLink(RegressionSuiteRef suite)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(suite.Title);
        return new ManualSuiteLink(
            suite.SuiteId,
            hasTitle ? suite.Title! : $"TC {suite.SuiteId}",
            suite.Url,
            hasTitle ? $"TC {suite.SuiteId} \u2014 {suite.Title}" : $"TC {suite.SuiteId} (no title resolved)",
            suite.IsLinked && !string.IsNullOrWhiteSpace(suite.Url));
    }

    /// <summary>Every manual test case as id/title/url TSV, including those behind the overflow chip.</summary>
    public string ManualSuitesClipboardText => string.Join(
        Environment.NewLine,
        ManualSuiteLinks.Select(l => $"{l.SuiteId}\t{l.Display}\t{l.Url}"));

    [RelayCommand]
    private void CopyManualSuites()
    {
        if (ManualSuiteLinks.Count == 0)
            return;

        try
        {
            Clipboard.SetText(ManualSuitesClipboardText);
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard; losing a copy must not take the grid down.
        }
    }

    public string EstimateText => Model.IsEstimate
        ? $"~{Model.EstimatedMinutes:0} min (est.)"
        : $"{Model.EstimatedMinutes:0} min";

    public string BuildNumber => Model.BuildNumber ?? "";
    public string BuildInfo => string.IsNullOrEmpty(Model.BuildNumber)
        ? ""
        : $"build {Model.BuildNumber}" + (Model.BuildFinishedUtc is { } f ? $" · {f.LocalDateTime:g}" : "");

    public string BuildResult => Model.BuildResult ?? "";
    public string LatestSuccessfulBuild => Model.LatestSuccessfulBuild ?? "\u2014";
    public string? LatestSuccessfulBuildUrl => Model.LatestSuccessfulBuildUrl;
    public bool HasLatestSuccessful => !string.IsNullOrEmpty(Model.LatestSuccessfulBuild);

    public string Repository => Model.Repository ?? "";
    public string? RepositoryUrl => Model.RepositoryUrl;
    public string DefaultBranch => Model.DefaultBranch ?? "";
    public string RepoInfo => string.IsNullOrEmpty(Model.Repository)
        ? ""
        : $"repo {Model.Repository}" + (string.IsNullOrEmpty(Model.DefaultBranch) ? "" : $" · branch {Model.DefaultBranch}");

    /// <summary>Distinct work items across all changes (build-level set) — for the detail pane and type filters.</summary>
    public IReadOnlyList<RegressionWorkItemRef> AllWorkItems => Model.Changes
        .SelectMany(c => c.WorkItems)
        .GroupBy(w => w.Id)
        .Select(g => g.First())
        .ToList();

    public bool HasWorkItems => AllWorkItems.Count > 0;

    /// <summary>Work items grouped by type (User Stories, Bug, IMS, Others), each ordered by created time, newest first.</summary>
    public IReadOnlyList<WorkItemGroup> WorkItemGroups
    {
        get
        {
            var all = AllWorkItems;
            var groups = new List<WorkItemGroup>();
            void Add(string label, Func<RegressionWorkItemRef, bool> match)
            {
                var items = all.Where(match)
                    .OrderByDescending(w => w.CreatedUtc ?? DateTimeOffset.MinValue)
                    .ToList();
                if (items.Count > 0)
                    groups.Add(new WorkItemGroup(label, items));
            }
            Add("User Stories", w => w.Kind == RegressionWorkItemKind.Story);
            Add("Bug", w => w.Kind == RegressionWorkItemKind.Bug);
            Add("IMS", w => w.Kind == RegressionWorkItemKind.Ims);
            Add("Others", w => w.Kind is not (RegressionWorkItemKind.Story or RegressionWorkItemKind.Bug or RegressionWorkItemKind.Ims));
            return groups;
        }
    }

    public bool HasBug => AllWorkItems.Any(w => w.Kind == RegressionWorkItemKind.Bug);
    public bool HasStory => AllWorkItems.Any(w => w.Kind == RegressionWorkItemKind.Story);
    public bool HasIms => AllWorkItems.Any(w => w.Kind == RegressionWorkItemKind.Ims);
    public string WorkItemSummary => AllWorkItems.Count == 0 ? "" : $"{AllWorkItems.Count} WI";

    public IReadOnlyList<string> RegressionAreas => Model.RegressionAreas ?? [];
    public IReadOnlyList<string> UseCases => Model.UseCases ?? [];
    public bool HasRegressionScope => RegressionAreas.Count > 0 || UseCases.Count > 0;
    public string RegressionAreasText => RegressionAreas.Count == 0 ? "\u2014" : string.Join(", ", RegressionAreas);
    public string UseCasesText => UseCases.Count == 0 ? "\u2014" : string.Join(", ", UseCases);

    /// <summary>Functional tests to execute for this component (use cases + regression areas), for the detail pane.</summary>
    public IReadOnlyList<string> FunctionalTests => UseCases
        .Concat(RegressionAreas)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    public string FunctionalTestsText => FunctionalTests.Count == 0 ? "\u2014" : string.Join(", ", FunctionalTests);

    /// <summary>1-based position in the current grid, assigned by the view-model after filtering.</summary>
    [ObservableProperty]
    private int _rowNumber;

    [ObservableProperty]
    private bool _isExpanded;

    // ── Impacted test cases (impact-mapping engine), lazily loaded on first expand ─────────────────

    /// <summary>Test cases the impact engine recommends for this component (shown in the row-expand detail).</summary>
    public ObservableCollection<ImpactedTestCaseRow> TestMatches { get; } = new();

    [ObservableProperty]
    private bool _isMatchesLoading;

    /// <summary>Loading/error note; empty once matches (or the fallback) are shown.</summary>
    [ObservableProperty]
    private string _matchesStatus = "";

    public bool HasTestMatches => TestMatches.Count > 0;

    /// <summary>Offline change summary shown when the engine matched no ADO test cases.</summary>
    [ObservableProperty]
    private string _changeSummary = "";

    /// <summary>Recommended functional tests (relevant-first), shown when the engine matched no test cases.</summary>
    public ObservableCollection<RecommendedTest> RecommendedTests { get; } = new();

    public bool HasRecommendedTests => RecommendedTests.Count > 0;

    /// <summary>True after load when nothing matched — surfaces the change summary + recommended tests fallback.</summary>
    [ObservableProperty]
    private bool _showChangeFallback;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_matchesLoaded && _matcher is not null)
            _ = LoadMatchesAsync();
    }

    private async Task LoadMatchesAsync()
    {
        if (_matcher is null || _matchesLoaded)
            return;

        IsMatchesLoading = true;
        MatchesStatus = "Analyzing changes\u2026";
        IReadOnlyList<ImpactedTestCaseMatch> matches = [];
        string? indexProblem = null;
        try
        {
            // Offload the engine cascade; the continuation resumes on the UI thread to touch the collection.
            matches = await Task.Run(() => _matcher.MatchAsync(Model, CancellationToken.None));
        }
        catch (ImpactIndexUnavailableException ex)
        {
            // Distinct from "no matches": the operator needs to know the index is the problem and how to fix it.
            indexProblem = ex.Message;
        }
        catch
        {
            // The matcher already logs; fall through to the offline change analysis below.
        }

        TestMatches.Clear();
        foreach (var m in matches)
            TestMatches.Add(new ImpactedTestCaseRow(
                m.TestCaseId, m.TestCaseTitle, m.TestCaseUrl,
                m.ParentFeatureId > 0 ? $"#{m.ParentFeatureId}" : "does not exist",
                m.MatchType, $"{m.ConfidencePercent}%", m.MatchReason, m.Description));

        // No ADO matches (empty/uninitialized index) — always surface the offline change summary + recommended tests.
        if (TestMatches.Count == 0)
        {
            ChangeSummary = RegressionChangeAnalyzer.Summarize(Model);
            RecommendedTests.Clear();
            foreach (var t in RegressionChangeAnalyzer.RecommendTests(Model))
                RecommendedTests.Add(t);
            ShowChangeFallback = true;
        }

        _matchesLoaded = indexProblem is null;   // retry on next expand once the index is rebuilt
        MatchesStatus = indexProblem ?? "";
        IsMatchesLoading = false;
        OnPropertyChanged(nameof(HasTestMatches));
        OnPropertyChanged(nameof(HasRecommendedTests));
    }
}

/// <summary>A modified file plus its Azure DevOps web link (null when the repository URL is unknown).</summary>
public sealed record ModifiedFileLink(string Path, string? Url);

/// <summary>A manual test case rendered as a titled link. <paramref name="IsLinked"/> is false when there is no URL to open.</summary>
public sealed record ManualSuiteLink(string SuiteId, string Display, string? Url, string Tooltip, bool IsLinked);

/// <summary>A named group of work items (by type) for the category-wise detail display.</summary>
public sealed record WorkItemGroup(string Label, IReadOnlyList<RegressionWorkItemRef> Items);

/// <summary>Display row for one impact-mapped test case in the grid's row-expand detail.</summary>
public sealed record ImpactedTestCaseRow(
    int TestCaseId, string Title, string? Url, string ParentFeatureText,
    string MatchType, string ConfidenceText, string MatchReason, string? Description);
