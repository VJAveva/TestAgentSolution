using System.Collections.Concurrent;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Real IRegressionDataProvider backed by live Azure DevOps queries (Phase E, replaces
/// MockRegressionDataProvider when Ado:Enabled=true). On-demand queries only — no SQLite
/// caching / background sync yet (deferred, see docs/rbac note in memory: this pass scopes to
/// real-time queries feeding the UI directly).
/// </summary>
public sealed class AdoRegressionDataProvider : IRegressionDataProvider
{
    private readonly IBuildQueries _builds;
    private readonly IGitQueries _git;
    private readonly IWorkItemQueries _workItems;
    private readonly ITestPlanQueries _testPlans;
    private readonly AdoChangeTranslator _translator;
    private readonly SpBuildImpactCollector _spCollector;
    private readonly ComponentChangeCollector _componentCollector;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;

    private readonly ConcurrentDictionary<string, RegressionSuiteEdit> _suiteEdits = new();
    private DateTimeOffset? _lastSyncUtc;
    private IReadOnlyList<string> _lastUnresolved = [];

    // Short-lived memo so GetConsolidated + GetScope in one UI load don't fetch ADO twice.
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private string? _memoKey;
    private DateTimeOffset _memoAt;
    private (IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved) _memo;

    public AdoRegressionDataProvider(
        IBuildQueries builds,
        IGitQueries git,
        IWorkItemQueries workItems,
        ITestPlanQueries testPlans,
        AdoChangeTranslator translator,
        SpBuildImpactCollector spCollector,
        ComponentChangeCollector componentCollector,
        Microsoft.Extensions.Options.IOptions<AdoOptions> options,
        IAppLogger logger)
    {
        _builds = builds;
        _git = git;
        _workItems = workItems;
        _testPlans = testPlans;
        _translator = translator;
        _spCollector = spCollector;
        _componentCollector = componentCollector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConsolidatedImpact> GetConsolidatedAsync(DateOnly? from, DateOnly? to, string? branch, CancellationToken ct)
    {
        var (rows, unresolved) = await FetchRowsAsync(from, to, branch, ct);
        _lastUnresolved = unresolved;
        _lastSyncUtc = DateTimeOffset.UtcNow;

        var summary = new RegressionSummary(
            ScopeLabel: from is null || to is null ? "Latest build" : "Custom",
            RangeText: from is { } f && to is { } t ? $"{f:yyyy-MM-dd} \u2013 {t:yyyy-MM-dd}" : "Latest build (current vs previous)",
            ChangeCount: rows.Sum(r => r.Changes.Count),
            SubsystemCount: rows.Count,
            FileCount: rows.Sum(r => r.TotalFilesModified),
            WeeklyActivity: []);

        return new ConsolidatedImpact(summary, rows);
    }

    public async Task<RegressionScope> GetScopeAsync(DateOnly? from, DateOnly? to, RegressionCategoryKind? category, string? branch, CancellationToken ct)
    {
        var (rows, unresolved) = await FetchRowsAsync(from, to, branch, ct);
        if (category is not null)
            rows = rows.Where(r => r.Category == category).ToList();

        var runtime = ToPlanColumn(rows.Where(r => r.Category is RegressionCategoryKind.Runtime or RegressionCategoryKind.Both));
        var config = ToPlanColumn(rows.Where(r => r.Category is RegressionCategoryKind.Config or RegressionCategoryKind.Both));

        return new RegressionScope(runtime, config, unresolved, ParallelAgentCount: 1);
    }

    public Task<RegressionSyncStatus> GetSyncStatusAsync(CancellationToken ct)
    {
        var state = _options.Enabled ? "live" : "disabled";
        return Task.FromResult(new RegressionSyncStatus(state, _lastSyncUtc, MapVersion: "v1", _lastUnresolved));
    }

    public Task ApplySuiteEditAsync(RegressionSuiteEdit edit, CancellationToken ct)
    {
        // Suite chip edits are not yet persisted against real ADO test suites (Phase E3 gap) \u2014
        // held in-memory only, same limitation noted in TestPlanQueries.cs.
        _suiteEdits[$"{edit.Subsystem}:{edit.IsManual}:{edit.SuiteId}"] = edit;
        _logger.Info("Ado", $"Suite edit recorded in-memory (not yet persisted to ADO): {edit.Subsystem} manual={edit.IsManual} suite={edit.SuiteId} remove={edit.Remove}");
        return Task.CompletedTask;
    }

    private async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> FetchRowsAsync(
        DateOnly? from, DateOnly? to, string? branch, CancellationToken ct)
    {
        // Memoize a single (from,to,branch) fetch for ~60s so GetConsolidated + GetScope don't double-scan ADO.
        var key = $"{_options.CollectionMode}|{branch}|{(from?.ToString("o") ?? "latest")}|{(to?.ToString("o") ?? "latest")}";
        await _fetchGate.WaitAsync(ct);
        try
        {
            if (_memoKey == key && DateTimeOffset.UtcNow - _memoAt < TimeSpan.FromSeconds(60))
                return _memo;

            var result = await FetchRowsUncachedAsync(from, to, branch, ct);
            _memoKey = key;
            _memoAt = DateTimeOffset.UtcNow;
            _memo = result;
            return result;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> FetchRowsUncachedAsync(
        DateOnly? from, DateOnly? to, string? branch, CancellationToken ct)
    {
        // Component-centric (default): a null window means "latest build per component" (current vs previous).
        if (_options.CollectionMode == AdoCollectionMode.Components)
        {
            var (rows, unresolved) = await _componentCollector.CollectAsync(from, to, branch, ct);
            return (MergeBySubsystem(rows.ToList()), unresolved);
        }

        // Legacy paths need a concrete window; default to the last week when none was supplied.
        var f = from ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
        var t = to ?? DateOnly.FromDateTime(DateTime.Today);

        // Legacy SP-manifest diff (kept for reference; does not apply to the Universal-Packages SP pipeline).
        if (_options.IsSpAnchored)
        {
            var (spRows, spUnresolved) = await _spCollector.CollectAsync(f, t, ct);
            return (MergeBySubsystem(spRows.ToList()), spUnresolved);
        }

        var repo = await _git.FindRepositoryByNameAsync(_options.Repository, ct);
        if (repo is null)
        {
            _logger.Warn("Ado", $"Repository '{_options.Repository}' not found in project '{_options.Project}' \u2014 returning empty result.");
            return ([], [_options.Repository]);
        }

        // Date-range fallback when no SP build definition is configured.
        var builds = await _builds.GetBuildsAsync(f, t, ct);
        var allRows = new List<SubsystemRow>();
        var allUnresolved = new HashSet<string>();

        foreach (var build in builds)
        {
            var changes = await _builds.GetBuildChangesAsync(build.Id, ct);
            var workItemIds = await _builds.GetBuildWorkItemIdsAsync(build.Id, ct);
            var workItems = await _workItems.GetByIdsAsync(workItemIds, ct);

            var filesByChange = new Dictionary<string, IReadOnlyList<(string Path, string ChangeType)>>();
            foreach (var change in changes)
            {
                if (change.Type != "commit") continue;
                var files = await _git.GetCommitChangesAsync(repo.Id, change.Id, ct);
                filesByChange[change.Id] = files;
            }

            var input = new TranslatorBuildInput(build.Id, changes, filesByChange, workItems);
            var (rows, unresolved) = _translator.Translate(input);
            allRows.AddRange(rows);
            foreach (var u in unresolved) allUnresolved.Add(u);
        }

        var merged = MergeBySubsystem(allRows);
        return (merged, allUnresolved.ToList());
    }

    private static List<SubsystemRow> MergeBySubsystem(List<SubsystemRow> rows)
    {
        return rows
            .GroupBy(r => (r.Component, r.Subsystem))
            .Select(g =>
            {
                var first = g.First();
                var allChanges = g.SelectMany(r => r.Changes).ToList();
                var allFiles = g.SelectMany(r => r.FilesModified).Distinct().ToList();
                return first with
                {
                    Changes = allChanges,
                    FilesModified = allFiles.Take(20).ToList(),
                    TotalFilesModified = allFiles.Count,
                };
            })
            .ToList();
    }

    private static RegressionPlanColumn ToPlanColumn(IEnumerable<SubsystemRow> rows)
    {
        var list = rows.ToList();
        return new RegressionPlanColumn(
            Subsystems: list.Count,
            AutomatedSuites: list.Sum(r => r.AutomatedSuites.Count),
            ManualSuites: list.Sum(r => r.ManualSuites.Count),
            Gaps: list.Count(r => r.AutomatedSuites.Count == 0 && r.ManualSuites.Count == 0),
            EstimatedMinutes: list.Sum(r => r.EstimatedMinutes));
    }
}
