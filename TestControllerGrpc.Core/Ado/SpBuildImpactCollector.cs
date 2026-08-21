using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// The modern equivalent of Program.cs's Main orchestration: anchor on the SP build definition, diff the
/// consumed-component version manifest between the latest two SP builds, and recurse one level into each
/// changed component's OMI build. Produces <see cref="SubsystemRow"/>s for the Regression grid.
/// </summary>
public sealed class SpBuildImpactCollector
{
    private readonly IBuildQueries _builds;
    private readonly IGitQueries _git;
    private readonly IWorkItemQueries _workItems;
    private readonly IBuildManifestSource _manifests;
    private readonly IComponentBuildMap _map;
    private readonly AdoChangeTranslator _translator;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;

    public SpBuildImpactCollector(
        IBuildQueries builds,
        IGitQueries git,
        IWorkItemQueries workItems,
        IBuildManifestSource manifests,
        IComponentBuildMap map,
        AdoChangeTranslator translator,
        IOptions<AdoOptions> options,
        IAppLogger logger)
    {
        _builds = builds;
        _git = git;
        _workItems = workItems;
        _manifests = manifests;
        _map = map;
        _translator = translator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> CollectAsync(CancellationToken ct) =>
        await CollectAsync(null, null, ct);

    /// <summary>
    /// Selects the SP builds to diff: when <paramref name="from"/>/<paramref name="to"/> are given, the SP builds
    /// that finished in that window (current = newest in window, previous = the build just before the window);
    /// otherwise the latest build vs its predecessor. Falls back to latest-vs-previous when the window is empty.
    /// </summary>
    public async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> CollectAsync(
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var spProject = _options.Project;
        var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? spProject : _options.OmiProject;
        var (spDefinitionId, spLogId) = _options.ResolveSpBuild();

        AdoBuildDto? current = null;
        AdoBuildDto? previous = null;

        if (from is { } f && to is { } t)
        {
            var inRange = await _builds.GetBuildsByDefinitionInRangeAsync(spProject, spDefinitionId, f, t, ct);
            if (inRange.Count > 0)
            {
                current = inRange[0];
                previous = inRange.Count > 1
                    ? inRange[^1]                                              // diff spans the whole window
                    : await _builds.GetPreviousBuildAsync(spProject, spDefinitionId, inRange[0].FinishTime ?? DateTimeOffset.MaxValue, ct);
                _logger.Info("Ado", $"SP builds in [{f}..{t}] for def {spDefinitionId}: {inRange.Count}. current={current.Id}, previous={previous?.Id.ToString() ?? "none"}.");
            }
            else
            {
                _logger.Info("Ado", $"No SP builds finished in [{f}..{t}] for def {spDefinitionId} — falling back to latest vs previous.");
            }
        }

        if (current is null)
        {
            var latest = await _builds.GetLatestBuildsByDefinitionAsync(
                spProject, spDefinitionId, Math.Max(2, _options.SpBuildHistoryCount), ct);
            if (latest.Count == 0)
            {
                _logger.Warn("Ado", $"No SP builds found for definition {spDefinitionId} in '{spProject}'.");
                return ([], []);
            }
            current = latest[0];
            previous = latest.Count > 1 ? latest[1] : null;
        }

        var rows = new List<SubsystemRow>();
        var unresolved = new HashSet<string>();

        // (a) The SP build's own git changes → file-path-resolved rows.
        var spRepo = await _git.FindRepositoryByNameAsync(spProject, _options.Repository, ct);
        if (spRepo is not null)
        {
            var (spRows, spUnresolved) = await TranslateBuildAsync(spProject, spRepo.Id, current.Id, ct);
            rows.AddRange(spRows);
            foreach (var u in spUnresolved) unresolved.Add(u);
            _logger.Info("Ado", $"SP build {current.Id} own changes: {spRows.Count} resolved row(s), {spUnresolved.Count} unresolved path hint(s).");
        }
        else
        {
            _logger.Warn("Ado", $"SP repository '{_options.Repository}' not found in '{spProject}'.");
            unresolved.Add(_options.Repository);
        }

        // (b) Consumed-component version diff between the latest two SP builds → the impacted components.
        if (previous is not null)
        {
            var currentManifest = await _manifests.GetManifestAsync(spProject, current.Id, spLogId, ct);
            var previousManifest = await _manifests.GetManifestAsync(spProject, previous.Id, spLogId, ct);
            var diff = BuildManifestDiffer.Diff(currentManifest, previousManifest);
            _logger.Info("Ado",
                $"SP manifest diff (build {current.Id} vs {previous.Id}): {diff.Changed.Count} consumed/changed, {diff.Unchanged.Count} unchanged.");

            foreach (var (component, version) in diff.Changed)
            {
                var info = _map.Resolve(component);
                var childRows = info is { BuildDefinitionId: > 0 }
                    ? await CollectOmiComponentRowsAsync(omiProject, component, info, ct)
                    : [];

                if (childRows.Count > 0)
                    rows.AddRange(childRows);
                else
                    rows.Add(VersionMoveRow(component, version, current.FinishTime));
            }
        }
        else
        {
            _logger.Warn("Ado", "Only one SP build available — cannot diff consumed versions (need at least two).");
        }

        return (rows, unresolved.ToList());
    }

    private async Task<List<SubsystemRow>> CollectOmiComponentRowsAsync(
        string omiProject, string component, ComponentBuildInfo info, CancellationToken ct)
    {
        var omiBuilds = await _builds.GetLatestBuildsByDefinitionAsync(omiProject, info.BuildDefinitionId, 2, ct);
        if (omiBuilds.Count == 0)
            return [];

        var current = omiBuilds[0];

        // Prefer the OMI build's OWN repository (authoritative from ADO — no guessing, ADR-06), then an
        // explicit vobs "repository" override, then the manifest component name.
        var repoId = current.Repository?.Id;
        if (repoId is null)
        {
            var repoName = info.Repository ?? component;
            var repo = await _git.FindRepositoryByNameAsync(omiProject, repoName, ct);
            if (repo is null)
            {
                _logger.Warn("Ado", $"OMI repository '{repoName}' not found in '{omiProject}' for component '{component}'.");
                return [];
            }
            repoId = repo.Id;
        }

        var (rows, _) = await TranslateBuildAsync(omiProject, repoId, current.Id, ct);
        return rows.ToList();
    }

    private async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> TranslateBuildAsync(
        string project, string repositoryId, int buildId, CancellationToken ct)
    {
        var changes = await _builds.GetBuildChangesAsync(project, buildId, ct);
        var workItemIds = await _builds.GetBuildWorkItemIdsAsync(project, buildId, ct);
        var workItems = await _workItems.GetByIdsAsync(workItemIds, ct);

        var filesByChange = new Dictionary<string, IReadOnlyList<(string Path, string ChangeType)>>();
        foreach (var change in changes)
        {
            if (change.Type != "commit") continue;
            filesByChange[change.Id] = await _git.GetCommitChangesAsync(project, repositoryId, change.Id, ct);
        }

        var fileCount = filesByChange.Values.Sum(f => f.Count);
        var changeTypes = string.Join(",", changes.Select(c => c.Type ?? "null").Distinct());
        _logger.Info("Ado", $"Build {buildId} (project '{project}'): {changes.Count} change(s) [types: {changeTypes}], {filesByChange.Count} commit(s) with files, {fileCount} file path(s), {workItems.Count} work item(s).");

        var input = new TranslatorBuildInput(buildId, changes, filesByChange, workItems);
        return _translator.Translate(input);
    }

    private static SubsystemRow VersionMoveRow(string component, string version, DateTimeOffset? observedUtc) => new(
        Component: component,
        Subsystem: component,
        Category: RegressionCategoryKind.Unclassified,
        CategoryConfidence: RegressionEvidenceKind.Observed,
        FilesModified: [],
        TotalFilesModified: 0,
        Changes:
        [
            new RegressionChangeRef(
                ChangeId: $"{component}:{version}",
                Summary: $"Consumed version moved to {version}",
                ObservedUtc: observedUtc ?? DateTimeOffset.UtcNow,
                FilePaths: [],
                WorkItems: [])
        ],
        RiskTier: "Unknown",
        AutomatedSuites: [],
        ManualSuites: [],
        EstimatedMinutes: 0,
        IsEstimate: true);
}
