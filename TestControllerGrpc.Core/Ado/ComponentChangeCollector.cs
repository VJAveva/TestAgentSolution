using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Component-centric change collection: for each component pipeline (from the vobs map), pulls its recent
/// build(s) in the requested window, their commits/PRs, work items and changed files, and emits one
/// <see cref="SubsystemRow"/> per component that changed. This replaces the SP-manifest model, which does
/// not apply to the Universal-Packages SP pipeline (confirmed against live ADO).
/// </summary>
public sealed class ComponentChangeCollector
{
    private const int MaxFileLookupsPerComponent = 8; // bound the per-commit file fetches for responsiveness
    private const int MaxBuildsPerComponentInRange = 12; // bound how many builds we aggregate per component in a window
    private const int MaxConcurrentComponentScans = 5; // scan components in parallel (bounded) so wide windows stay responsive
    private const int MaxCommitsPerComponent = 200; // cap branch-history commits scanned per component
    private const int MaxPrsPerComponent = 40; // cap PRs (and their work-item lookups) scanned per component

    private readonly IBuildQueries _builds;
    private readonly IGitQueries _git;
    private readonly IWorkItemQueries _workItems;
    private readonly IComponentBuildMap _map;
    private readonly IComponentCategorizer _categorizer;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;

    // Cached OMI repo map (name -> id + default branch), fetched once per process.
    private readonly SemaphoreSlim _repoMapGate = new(1, 1);
    private Dictionary<string, (string Id, string? Branch)>? _repoMap;

    // Cached .sln names per repo id (Subsystems column), fetched once per repo per process.
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> _slnByRepo = new(StringComparer.OrdinalIgnoreCase);

    public ComponentChangeCollector(
        IBuildQueries builds,
        IGitQueries git,
        IWorkItemQueries workItems,
        IComponentBuildMap map,
        IComponentCategorizer categorizer,
        IOptions<AdoOptions> options,
        IAppLogger logger)
    {
        _builds = builds;
        _git = git;
        _workItems = workItems;
        _map = map;
        _categorizer = categorizer;
        _options = options.Value;
        _logger = logger;
    }

    private async Task<Dictionary<string, (string Id, string? Branch)>> GetRepoMapAsync(string project, CancellationToken ct)
    {
        if (_repoMap is not null)
            return _repoMap;
        await _repoMapGate.WaitAsync(ct);
        try
        {
            if (_repoMap is null)
            {
                var repos = await _git.GetAllRepositoriesAsync(project, ct);
                _repoMap = repos
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => (g.First().Id, g.First().DefaultBranch), StringComparer.OrdinalIgnoreCase);
                _logger.Info("Ado", $"Repo map cached: {_repoMap.Count} repositories in '{project}'.");
            }
            return _repoMap;
        }
        finally
        {
            _repoMapGate.Release();
        }
    }

    public async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> CollectAsync(
        DateOnly? from, DateOnly? to, string? branch, CancellationToken ct)
    {
        var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
        var components = _map.All().Where(c => c.BuildDefinitionId > 0).ToList();
        _logger.Info("Ado", $"Component scan: {components.Count} components in '{omiProject}', window={(from.HasValue ? $"{from}..{to}" : "latest")}, concurrency={MaxConcurrentComponentScans}.");

        var rows = new ConcurrentBag<SubsystemRow>();
        using var gate = new SemaphoreSlim(MaxConcurrentComponentScans);

        async Task ScanAsync(ComponentBuildInfo comp)
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var builds = from is { } f && to is { } t
                    ? await _builds.GetBuildsByDefinitionInRangeAsync(omiProject, comp.BuildDefinitionId, f, t, branch, ct)
                    : await _builds.GetLatestBuildsByDefinitionAsync(omiProject, comp.BuildDefinitionId, 1, branch, ct);
                if (builds.Count == 0)
                    return;

                var latestSuccessful = builds.FirstOrDefault(b => string.Equals(b.Result, "succeeded", StringComparison.OrdinalIgnoreCase));
                // Aggregate churn across ALL builds in the window so a wider timeline surfaces more changes;
                // fall back to the single latest build only when no window was supplied.
                var row = from is not null && to is not null
                    ? await BuildComponentRowForRangeAsync(omiProject, comp, builds, latestSuccessful, ct)
                    : await BuildComponentRowAsync(omiProject, comp, builds[0], latestSuccessful, skipIfNoChanges: true, ct);
                if (row is not null)
                    rows.Add(row); // only surface components that actually changed
            }
            catch (AdoApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // Auth failures affect every component — surface them instead of silently returning an empty grid.
                _logger.Error("Ado", "ADO auth failed while scanning components — aborting scan.", ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Ado", $"Component '{comp.ComponentId}' (def {comp.BuildDefinitionId}) scan failed: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(components.Select(ScanAsync));
        }
        catch (AdoApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw; // auth failure affects every component — surface it to the caller
        }

        // ConcurrentBag is unordered; sort by component for a stable, readable grid.
        var list = rows.OrderBy(r => r.Component, StringComparer.OrdinalIgnoreCase).ToList();
        _logger.Info("Ado", $"Component scan complete: {list.Count} component(s) changed of {components.Count}.");
        return (list, []);
    }

    /// <summary>
    /// Git-centric branch scan: for each component, everything created on <paramref name="branch"/> since its
    /// FIRST build (the branch's "created" date) — commits, PRs, PR-linked work items and changed files.
    /// Emits one row per component that had activity on the branch.
    /// </summary>
    public async Task<(IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> Unresolved)> CollectBranchSinceCreationAsync(
        string branch, CancellationToken ct)
    {
        var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
        var components = _map.All().Where(c => c.BuildDefinitionId > 0).ToList();
        _logger.Info("Ado", $"Branch scan: {components.Count} components on '{branch}' since first build, concurrency={MaxConcurrentComponentScans}.");

        var rows = new ConcurrentBag<SubsystemRow>();
        using var gate = new SemaphoreSlim(MaxConcurrentComponentScans);

        async Task ScanAsync(ComponentBuildInfo comp)
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                // Window start = the branch's first build; no build on the branch => the component never ran there.
                var firstBuild = await _builds.GetFirstBuildOnBranchAsync(omiProject, comp.BuildDefinitionId, branch, ct);
                if (firstBuild is null)
                    return;
                var latest = await _builds.GetLatestBuildsByDefinitionAsync(omiProject, comp.BuildDefinitionId, 1, branch, ct);
                var headerBuild = latest.Count > 0 ? latest[0] : firstBuild;
                var since = firstBuild.StartTime ?? firstBuild.FinishTime ?? DateTimeOffset.UtcNow.AddYears(-1);

                string? repoId = null;
                string? defaultBranch = null;
                if (!string.IsNullOrWhiteSpace(comp.Repository))
                {
                    var repoMap = await GetRepoMapAsync(omiProject, ct);
                    if (repoMap.TryGetValue(comp.Repository, out var hit))
                        (repoId, defaultBranch) = hit;
                }
                repoId ??= headerBuild.Repository?.Id;
                if (string.IsNullOrEmpty(repoId))
                    return;

                var row = await BuildBranchRowAsync(omiProject, comp, headerBuild, firstBuild, repoId, branch, since, defaultBranch, ct);
                if (row is not null)
                    rows.Add(row);
            }
            catch (AdoApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.Error("Ado", "ADO auth failed while scanning branch — aborting scan.", ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Ado", $"Component '{comp.ComponentId}' (def {comp.BuildDefinitionId}) branch scan failed: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(components.Select(ScanAsync));
        }
        catch (AdoApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw;
        }

        var branchList = rows.OrderBy(r => r.Component, StringComparer.OrdinalIgnoreCase).ToList();
        _logger.Info("Ado", $"Branch scan complete: {branchList.Count} component(s) with activity of {components.Count}.");
        return (branchList, []);
    }

    private async Task<SubsystemRow?> BuildBranchRowAsync(
        string omiProject, ComponentBuildInfo comp, AdoBuildDto headerBuild, AdoBuildDto firstBuild,
        string repoId, string branch, DateTimeOffset since, string? defaultBranch, CancellationToken ct)
    {
        var changeRefs = new List<RegressionChangeRef>();
        var allFiles = new List<string>();

        // Commits on the branch since the window start.
        var commits = await _git.GetCommitsOnBranchSinceAsync(omiProject, repoId, branch, since, MaxCommitsPerComponent, ct);
        var fileLookups = 0;
        foreach (var c in commits)
        {
            var when = c.Author?.Date ?? c.Committer?.Date ?? headerBuild.FinishTime ?? DateTimeOffset.UtcNow;
            var paths = fileLookups < MaxFileLookupsPerComponent
                ? await SafeGetFilesAsync(omiProject, repoId, c.CommitId, ct)
                : [];
            if (paths.Count > 0) fileLookups++;
            allFiles.AddRange(paths);
            changeRefs.Add(new RegressionChangeRef(
                ChangeId: c.CommitId,
                Summary: c.Comment ?? "(no message)",
                ObservedUtc: when,
                FilePaths: paths,
                WorkItems: [],
                Kind: ClassifyChange(c.Comment),
                Url: c.RemoteUrl));
        }

        // PRs targeting the branch, created on/after the window start; work items come from the PRs.
        var prs = (await _git.GetPullRequestsTargetingBranchAsync(omiProject, repoId, branch, MaxPrsPerComponent, ct))
            .Where(p => (p.CreationDate ?? DateTimeOffset.MaxValue) >= since)
            .ToList();
        var manualSuitesForRow = new List<RegressionSuiteRef>();
        foreach (var pr in prs)
        {
            var prWiIds = await _git.GetPullRequestWorkItemIdsAsync(omiProject, repoId, pr.PullRequestId, ct);
            var prWorkItems = prWiIds.Count == 0
                ? new List<RegressionWorkItemRef>()
                : (await _workItems.GetWithRelationsAsync(prWiIds, ct)).Select(ToWorkItemRef).ToList();
            manualSuitesForRow.AddRange(await GetManualSuitesAsync(prWiIds, ct));
            changeRefs.Add(new RegressionChangeRef(
                ChangeId: $"PR-{pr.PullRequestId}",
                Summary: pr.Title ?? $"PR {pr.PullRequestId}",
                ObservedUtc: pr.CreationDate ?? headerBuild.FinishTime ?? DateTimeOffset.UtcNow,
                FilePaths: [],
                WorkItems: prWorkItems,
                Kind: RegressionChangeKind.PullRequest,
                Url: PullRequestWebUrl(omiProject, comp.Repository ?? headerBuild.Repository?.Name, pr.PullRequestId)));
        }

        if (changeRefs.Count == 0)
            return null; // no activity on this branch for the component

        var solutionNames = await GetSolutionNamesCachedAsync(omiProject, repoId, ct);
        var latestSuccessful = headerBuild.Result == "succeeded" ? headerBuild : null;
        return BuildRow(omiProject, comp, headerBuild, latestSuccessful, changeRefs, allFiles.Distinct().ToList(), defaultBranch, solutionNames, manualSuitesForRow);
    }

    private string? PullRequestWebUrl(string project, string? repo, int prId) =>
        string.IsNullOrWhiteSpace(repo)
            ? null
            : $"https://dev.azure.com/{_options.Organization}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}/pullrequest/{prId}";

    /// <summary>Recent builds for one component definition, newest first — for the build picker dropdown.</summary>
    public async Task<IReadOnlyList<RegressionBuildRef>> GetComponentBuildsAsync(int definitionId, int top, CancellationToken ct)
    {
        var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
        var builds = await _builds.GetLatestBuildsByDefinitionAsync(omiProject, definitionId, top, null, ct);
        _logger.Info("Ado", $"Build picker: {builds.Count} build(s) for def {definitionId} in '{omiProject}'.");
        return builds.Select(b => new RegressionBuildRef(b.Id, b.BuildNumber, b.Result ?? "", b.FinishTime)).ToList();
    }

    /// <summary>Impact for one specifically-picked build of a component (build picker re-fetch).</summary>
    public async Task<SubsystemRow?> CollectComponentBuildAsync(int definitionId, int buildId, CancellationToken ct)
    {
        var comp = _map.All().FirstOrDefault(c => c.BuildDefinitionId == definitionId);
        if (comp is null)
            return null;
        var omiProject = string.IsNullOrWhiteSpace(_options.OmiProject) ? _options.Project : _options.OmiProject;
        var build = await _builds.GetBuildAsync(omiProject, buildId, ct);
        if (build is null)
            return null;
        return await BuildComponentRowAsync(omiProject, comp, build, build.Result == "succeeded" ? build : null, skipIfNoChanges: false, ct);
    }

    private async Task<SubsystemRow?> BuildComponentRowAsync(
        string omiProject, ComponentBuildInfo comp, AdoBuildDto build, AdoBuildDto? latestSuccessful, bool skipIfNoChanges, CancellationToken ct)
    {
        var changes = await _builds.GetBuildChangesAsync(omiProject, build.Id, ct);
        if (changes.Count == 0 && skipIfNoChanges)
            return null;

        var workItemIds = await _builds.GetBuildWorkItemIdsAsync(omiProject, build.Id, ct);
        var workItems = await _workItems.GetWithRelationsAsync(workItemIds, ct);
        var workItemRefs = workItems.Select(ToWorkItemRef).ToList();
        var manualSuites = await GetManualSuitesAsync(workItems, ct);

        // Resolve the component's repo explicitly from the configured name; fall back to the build's own repo.
        string? repoId = null;
        string? defaultBranch = null;
        if (!string.IsNullOrWhiteSpace(comp.Repository))
        {
            var repoMap = await GetRepoMapAsync(omiProject, ct);
            if (repoMap.TryGetValue(comp.Repository, out var hit))
                (repoId, defaultBranch) = hit;
        }
        repoId ??= build.Repository?.Id;

        var allFiles = new List<string>();
        var changeRefs = new List<RegressionChangeRef>();
        var fileLookups = 0;
        foreach (var ch in changes)
        {
            // Cap per-commit file lookups so a component with dozens of commits doesn't stall the load.
            var paths = fileLookups < MaxFileLookupsPerComponent
                ? await SafeGetFilesAsync(omiProject, repoId, ch.Id, ct)
                : [];
            if (paths.Count > 0) fileLookups++;
            allFiles.AddRange(paths);
            changeRefs.Add(new RegressionChangeRef(
                ChangeId: ch.Id,
                Summary: ch.Message ?? "(no message)",
                ObservedUtc: ch.Timestamp ?? build.FinishTime ?? DateTimeOffset.UtcNow,
                FilePaths: paths,
                WorkItems: workItemRefs,
                Kind: ClassifyChange(ch.Message),
                Url: ch.DisplayUri));
        }

        var solutionNames = await GetSolutionNamesCachedAsync(omiProject, repoId, ct);
        return BuildRow(omiProject, comp, build, latestSuccessful, changeRefs, allFiles.Distinct().ToList(), defaultBranch, solutionNames, manualSuites);
    }

    /// <summary>
    /// Aggregates changes/files/work-items across every build in the requested window into one row, so a
    /// wider Timeline actually surfaces more churn. The newest build supplies the row's header fields.
    /// </summary>
    private async Task<SubsystemRow?> BuildComponentRowForRangeAsync(
        string omiProject, ComponentBuildInfo comp, IReadOnlyList<AdoBuildDto> builds, AdoBuildDto? latestSuccessful, CancellationToken ct)
    {
        var headerBuild = builds[0]; // newest first (queryOrder=finishTimeDescending)

        // Resolve the component's repo once — it's independent of the individual build.
        string? repoId = null;
        string? defaultBranch = null;
        if (!string.IsNullOrWhiteSpace(comp.Repository))
        {
            var repoMap = await GetRepoMapAsync(omiProject, ct);
            if (repoMap.TryGetValue(comp.Repository, out var hit))
                (repoId, defaultBranch) = hit;
        }
        repoId ??= headerBuild.Repository?.Id;

        var allFiles = new List<string>();
        var changeRefs = new List<RegressionChangeRef>();
        var seenChangeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileLookups = 0;
        var manualSuitesForRow = new List<RegressionSuiteRef>();

        foreach (var build in builds.Take(MaxBuildsPerComponentInRange))
        {
            ct.ThrowIfCancellationRequested();
            var changes = await _builds.GetBuildChangesAsync(omiProject, build.Id, ct);
            if (changes.Count == 0)
                continue;

            var workItemIds = await _builds.GetBuildWorkItemIdsAsync(omiProject, build.Id, ct);
            var workItems = await _workItems.GetWithRelationsAsync(workItemIds, ct);
            var workItemRefs = workItems.Select(ToWorkItemRef).ToList();
            var manualSuites = await GetManualSuitesAsync(workItems, ct);
            manualSuitesForRow.AddRange(manualSuites);

            foreach (var ch in changes)
            {
                if (!seenChangeIds.Add(ch.Id))
                    continue; // the same commit can appear on adjacent builds — count it once

                var paths = fileLookups < MaxFileLookupsPerComponent
                    ? await SafeGetFilesAsync(omiProject, repoId, ch.Id, ct)
                    : [];
                if (paths.Count > 0) fileLookups++;
                allFiles.AddRange(paths);
                changeRefs.Add(new RegressionChangeRef(
                    ChangeId: ch.Id,
                    Summary: ch.Message ?? "(no message)",
                    ObservedUtc: ch.Timestamp ?? build.FinishTime ?? DateTimeOffset.UtcNow,
                    FilePaths: paths,
                    WorkItems: workItemRefs,
                    Kind: ClassifyChange(ch.Message),
                    Url: ch.DisplayUri));
            }
        }

        if (changeRefs.Count == 0)
            return null; // component didn't actually change anywhere in the window

        var solutionNames = await GetSolutionNamesCachedAsync(omiProject, repoId, ct);
        return BuildRow(omiProject, comp, headerBuild, latestSuccessful, changeRefs, allFiles.Distinct().ToList(), defaultBranch, solutionNames, manualSuitesForRow);
    }

    private async Task<IReadOnlyList<string>> SafeGetFilesAsync(string project, string? repoId, string commitId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(commitId))
            return [];
        try
        {
            var files = await _git.GetCommitChangesAsync(project, repoId, commitId, ct);
            // Drop package-manifest / pipeline noise (Universal-Package.json, *.yml, etc.) so only real
            // source/interface changes surface in the grid, report, and LLM grounding.
            return files
                .Select(f => f.Path)
                .Where(p => !FileNoiseFilter.IsIgnored(p, null, _options.IgnoredFilePatterns))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"Commit {commitId[..Math.Min(8, commitId.Length)]} file lookup failed: {ex.Message}");
            return [];
        }
    }

    private Task<IReadOnlyList<string>> GetSolutionNamesCachedAsync(string project, string? repoId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoId))
            return Task.FromResult<IReadOnlyList<string>>([]);
        return _slnByRepo.GetOrAdd(repoId, id => _git.GetSolutionNamesAsync(project, id, ct));
    }

    private async Task<IReadOnlyList<RegressionSuiteRef>> GetManualSuitesAsync(IReadOnlyList<int> workItemIds, CancellationToken ct)
    {
        if (workItemIds.Count == 0)
            return [];

        var workItems = await _workItems.GetWithRelationsAsync(workItemIds, ct);
        return await GetManualSuitesAsync(workItems, ct);
    }

    private async Task<IReadOnlyList<RegressionSuiteRef>> GetManualSuitesAsync(IReadOnlyList<AdoWorkItemDto> workItems, CancellationToken ct)
    {
        var relationUrls = workItems
            .SelectMany(w => w.Relations)
            .Where(r => string.Equals(r.Rel, "Microsoft.VSTS.Common.TestedBy-Forward", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (relationUrls.Count == 0)
            return [];

        var ids = relationUrls
            .Select(url => url is not null && int.TryParse(url.AsSpan(url.LastIndexOf('/') + 1), out int id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return [];

        var testCases = await _workItems.GetByIdsAsync(ids, ct);
        var byId = testCases.ToDictionary(w => w.Id);
        return ids.Select(id => byId.TryGetValue(id, out var testCase)
                ? new RegressionSuiteRef(id.ToString(), true, testCase.Url, RegressionEvidenceKind.Observed, testCase.Title)
                : new RegressionSuiteRef(id.ToString(), true, relationUrls.FirstOrDefault(url => url!.EndsWith('/' + id.ToString(), StringComparison.Ordinal))!, RegressionEvidenceKind.Observed, $"Test Case {id}"))
            .ToList();
    }

    private SubsystemRow BuildRow(string omiProject, ComponentBuildInfo comp, AdoBuildDto build, AdoBuildDto? latestSuccessful, List<RegressionChangeRef> changes, List<string> files, string? defaultBranch, IReadOnlyList<string> solutionNames, IReadOnlyList<RegressionSuiteRef> manualSuites)
    {
        // Category comes from docs/impact/component-categories.xml; Declared when found, Assumed otherwise.
        var category = _categorizer.Categorize(comp.ComponentId);
        var confidence = _categorizer.IsClassified(comp.ComponentId)
            ? RegressionEvidenceKind.Declared
            : RegressionEvidenceKind.Observed;

        return new SubsystemRow(
            Component: comp.ComponentId,
            Subsystem: comp.ComponentId,
            Category: category,
            CategoryConfidence: confidence,
            FilesModified: files.Take(25).ToList(),
            TotalFilesModified: files.Count,
            Changes: changes,
            RiskTier: build.Result ?? "unknown",
            AutomatedSuites: [],
            ManualSuites: manualSuites.DistinctBy(s => s.SuiteId).ToList(),
            EstimatedMinutes: 0,
            IsEstimate: true,
            RegressionAreas: comp.RegressionAreas,
            UseCases: comp.UseCases,
            BuildNumber: build.BuildNumber,
            BuildFinishedUtc: build.FinishTime,
            BuildResult: build.Result,
            LatestSuccessfulBuild: latestSuccessful?.BuildNumber,
            LatestSuccessfulBuildUrl: latestSuccessful is null ? null : BuildWebUrl(omiProject, latestSuccessful.Id),
            Repository: comp.Repository ?? build.Repository?.Name,
            RepositoryUrl: RepoWebUrl(omiProject, comp.Repository ?? build.Repository?.Name),
            DefaultBranch: NormalizeBranch(defaultBranch),
            SolutionNames: solutionNames);
    }

    private static string? NormalizeBranch(string? refName) =>
        string.IsNullOrEmpty(refName) ? null : refName.Replace("refs/heads/", "");


    private string? RepoWebUrl(string project, string? repo) =>
        string.IsNullOrWhiteSpace(repo)
            ? null
            : $"https://dev.azure.com/{_options.Organization}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}";

    private string BuildWebUrl(string project, int buildId) =>
        $"https://dev.azure.com/{_options.Organization}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId}&view=results";

    private string WorkItemWebUrl(int id) =>
        $"https://dev.azure.com/{_options.Organization}/_workitems/edit/{id}";

    private RegressionWorkItemRef ToWorkItemRef(AdoWorkItemDto dto) => new(
        Id: dto.Id,
        Kind: ParseWorkItemKind(dto.WorkItemType),
        Title: dto.Title ?? $"Work item {dto.Id}",
        Url: WorkItemWebUrl(dto.Id),
        CreatedUtc: dto.CreatedUtc,
        WorkItemType: dto.WorkItemType);

    private static RegressionChangeKind ClassifyChange(string? message)
    {
        if (string.IsNullOrEmpty(message)) return RegressionChangeKind.Commit;
        if (message.Contains("Merged PR", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Merge pull request", StringComparison.OrdinalIgnoreCase))
            return RegressionChangeKind.PullRequest;
        // Automated build-syncup / version-bump commits produced by the build service.
        if (message.Contains("[BuildTool", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Build Syncup", StringComparison.OrdinalIgnoreCase))
            return RegressionChangeKind.Automated;
        return RegressionChangeKind.Commit;
    }

    private static RegressionWorkItemKind ParseWorkItemKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return RegressionWorkItemKind.Other;
        // IMS types vary by process template (e.g. "Internal IMS Request", "IMS Internal Request") — match the token.
        if (type.Contains("IMS", StringComparison.OrdinalIgnoreCase))
            return RegressionWorkItemKind.Ims;
        return type switch
        {
            "Bug" => RegressionWorkItemKind.Bug,
            "User Story" => RegressionWorkItemKind.Story,
            "Feature" => RegressionWorkItemKind.Feature,
            "Issue" => RegressionWorkItemKind.Ims,
            _ => RegressionWorkItemKind.Other,
        };
    }
}
