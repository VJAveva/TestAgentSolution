using TestControllerGrpc.Ado.Dto;

namespace TestControllerGrpc.Ado;

public interface IBuildQueries
{
    /// <summary>Builds completed within [from, to], newest first.</summary>
    Task<IReadOnlyList<AdoBuildDto>> GetBuildsAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// The latest <paramref name="top"/> completed builds for one definition, newest first — the legacy
    /// "SP build" selection (definitions={id}&amp;$top=N&amp;queryOrder=finishTimeDescending).
    /// </summary>
    Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(int definitionId, int top, CancellationToken ct);

    /// <summary>Definition-anchored builds in an explicit project (e.g. the OMI project).</summary>
    Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, CancellationToken ct);

    /// <summary>Definition-anchored builds in a project, optionally filtered to one branch (refs/heads/{branch}).</summary>
    Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, string? branchName, CancellationToken ct);

    /// <summary>Oldest completed build of a definition on a branch (the branch's first build — used as the "created" date).</summary>
    Task<AdoBuildDto?> GetFirstBuildOnBranchAsync(string project, int definitionId, string branchName, CancellationToken ct);

    /// <summary>Completed builds for a definition that finished within [from, to], newest first.</summary>
    Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>As above, optionally filtered to one branch (refs/heads/{branch}) for parallel-release tracking.</summary>
    Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, string? branchName, CancellationToken ct);

    /// <summary>All build definitions in a project (for the UI definition picker). Newest/name order per ADO.</summary>
    Task<IReadOnlyList<AdoBuildDefinitionDto>> GetBuildDefinitionsAsync(string project, CancellationToken ct);

    /// <summary>Distinct source branches (short names) from the most recent builds in a project — for the branch switcher.</summary>
    Task<IReadOnlyList<string>> GetRecentSourceBranchesAsync(string project, int top, CancellationToken ct);

    /// <summary>The newest completed build for a definition that finished strictly before <paramref name="before"/>.</summary>
    Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, CancellationToken ct);

    /// <summary>
    /// As above, scoped to one branch and optionally to succeeded builds only. Churn baselines MUST use this:
    /// the unscoped overload can return another branch's build, which yields a diff spanning the divergence.
    /// </summary>
    Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, string? branchName, bool succeededOnly, CancellationToken ct);

    /// <summary>A single build by id (for the build picker's re-fetch of a specific build).</summary>
    Task<AdoBuildDto?> GetBuildAsync(string project, int buildId, CancellationToken ct);

    /// <summary>GET builds/{id}/changes — commits/PRs since the previous build (ADR-05, replaces log-scraping).</summary>
    Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(int buildId, CancellationToken ct);

    /// <summary>GET builds/{id}/changes in an explicit project.</summary>
    Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(string project, int buildId, CancellationToken ct);

    /// <summary>Work item ids linked to a build.</summary>
    Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(int buildId, CancellationToken ct);

    /// <summary>Work item ids linked to a build in an explicit project.</summary>
    Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(string project, int buildId, CancellationToken ct);
}

public sealed class BuildQueries : IBuildQueries
{
    private const int ChangePageSize = 200;  // ADO caps builds/{id}/changes at 200 per page
    private const int MaxChangePages = 20;   // 4000 commits is far past useful churn; stop rather than loop forever

    private readonly AdoClient _client;

    public BuildQueries(AdoClient client) => _client = client;

    public async Task<IReadOnlyList<AdoBuildDto>> GetBuildsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(
            $"build/builds?minTime={from:yyyy-MM-dd}&maxTime={to:yyyy-MM-dd}&statusFilter=completed&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(int definitionId, int top, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(
            $"build/builds?definitions={definitionId}&$top={top}&statusFilter=completed&queryOrder=finishTimeDescending&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value;
    }

    public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, CancellationToken ct)
        => GetLatestBuildsByDefinitionAsync(project, definitionId, top, null, ct);

    public async Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, string? branchName, CancellationToken ct)
    {
        var branch = string.IsNullOrWhiteSpace(branchName) ? "" : $"&branchName=refs/heads/{Uri.EscapeDataString(branchName)}";
        var path = _client.ProjectApiPath(project,
            $"build/builds?definitions={definitionId}&$top={top}{branch}&statusFilter=completed&queryOrder=finishTimeDescending&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value;
    }

    public async Task<AdoBuildDto?> GetFirstBuildOnBranchAsync(string project, int definitionId, string branchName, CancellationToken ct)
    {
        var branch = string.IsNullOrWhiteSpace(branchName) ? "" : $"&branchName=refs/heads/{Uri.EscapeDataString(branchName)}";
        var path = _client.ProjectApiPath(project,
            $"build/builds?definitions={definitionId}&$top=1{branch}&statusFilter=completed&queryOrder=finishTimeAscending&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value.FirstOrDefault();
    }

    public Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, CancellationToken ct)
        => GetBuildsByDefinitionInRangeAsync(project, definitionId, from, to, null, ct);

    public async Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, string? branchName, CancellationToken ct)
    {
        // maxTime is the day AFTER 'to' so the whole 'to' day is included.
        var branch = string.IsNullOrWhiteSpace(branchName) ? "" : $"&branchName=refs/heads/{Uri.EscapeDataString(branchName)}";
        var path = _client.ProjectApiPath(project,
            $"build/builds?definitions={definitionId}&minTime={from:yyyy-MM-dd}&maxTime={to.AddDays(1):yyyy-MM-dd}{branch}&statusFilter=completed&queryOrder=finishTimeDescending&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<AdoBuildDefinitionDto>> GetBuildDefinitionsAsync(string project, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project, "build/definitions?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDefinitionDto>>(path, ct);
        return result.Value;
    }

    public Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, CancellationToken ct)
        => GetPreviousBuildAsync(project, definitionId, before, null, false, ct);

    public async Task<AdoBuildDto?> GetPreviousBuildAsync(
        string project, int definitionId, DateTimeOffset before, string? branchName, bool succeededOnly, CancellationToken ct)
    {
        var branch = string.IsNullOrWhiteSpace(branchName) ? "" : $"&branchName=refs/heads/{Uri.EscapeDataString(branchName)}";
        var result = succeededOnly ? "&resultFilter=succeeded" : "";
        var path = _client.ProjectApiPath(project,
            $"build/builds?definitions={definitionId}&maxTime={before:o}&$top=2{branch}{result}&statusFilter=completed&queryOrder=finishTimeDescending&api-version=7.1");
        var page = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        // maxTime is inclusive, so the first entry may be the boundary build itself — take the first that finished strictly earlier.
        return page.Value.FirstOrDefault(b => b.FinishTime is { } f && f < before);
    }

    public async Task<AdoBuildDto?> GetBuildAsync(string project, int buildId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project, $"build/builds/{buildId}?api-version=7.1");
        try { return await _client.GetAsync<AdoBuildDto>(path, ct); }
        catch (AdoApiException) { return null; }
    }

    public async Task<IReadOnlyList<string>> GetRecentSourceBranchesAsync(string project, int top, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project,
            $"build/builds?$top={top}&queryOrder=finishTimeDescending&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoBuildDto>>(path, ct);
        return result.Value
            .Select(b => b.SourceBranch)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Replace("refs/heads/", "", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(int buildId, CancellationToken ct)
        => GetBuildChangesPagedAsync(_client.ProjectApiPath($"build/builds/{buildId}/changes"), ct);

    public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(string project, int buildId, CancellationToken ct)
        => GetBuildChangesPagedAsync(_client.ProjectApiPath(project, $"build/builds/{buildId}/changes"), ct);

    // Without $top ADO serves a single default-sized page and the rest is silently lost, which reads as
    // "that build changed nothing" rather than as an error.
    private async Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesPagedAsync(string basePath, CancellationToken ct)
    {
        var all = new List<AdoBuildChangeDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? continuation = null;

        for (var page = 0; page < MaxChangePages; page++)
        {
            var token = continuation is null ? "" : $"&continuationToken={Uri.EscapeDataString(continuation)}";
            var path = $"{basePath}?$top={ChangePageSize}{token}&api-version=7.1";
            var (body, next) = await _client.GetWithContinuationAsync<AdoListResponse<AdoBuildChangeDto>>(path, ct);

            foreach (var change in body.Value)
                if (seen.Add(change.Id))
                    all.Add(change);

            if (string.IsNullOrWhiteSpace(next) || body.Value.Count == 0)
                return all;
            continuation = next;
        }

        return all;
    }

    public async Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(int buildId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath($"build/builds/{buildId}/workitems?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoWorkItemRefDto>>(path, ct);
        return result.Value.Select(w => int.Parse(w.Id)).ToList();
    }

    public async Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(string project, int buildId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project, $"build/builds/{buildId}/workitems?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoWorkItemRefDto>>(path, ct);
        return result.Value.Select(w => int.Parse(w.Id)).ToList();
    }
}

public sealed class AdoWorkItemRefDto
{
    public string Id { get; set; } = "0";
}
