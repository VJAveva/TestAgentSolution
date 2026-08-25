using TestControllerGrpc.Ado.Dto;

namespace TestControllerGrpc.Ado;

public interface IGitQueries
{
    /// <summary>Changed file paths for a single commit (used to enrich a build change with file lists).</summary>
    Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(string repositoryId, string commitId, CancellationToken ct);

    /// <summary>Changed file paths for a single commit in an explicit project.</summary>
    Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(string project, string repositoryId, string commitId, CancellationToken ct);

    /// <summary>Resolves a repository by exact name match. Returns null if not found — never guess (ADR-06).</summary>
    Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string name, CancellationToken ct);

    /// <summary>Resolves a repository by exact name match in an explicit project.</summary>
    Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string project, string name, CancellationToken ct);

    /// <summary>All repositories in a project (for the component repo map: name -> id + default branch).</summary>
    Task<IReadOnlyList<AdoRepositoryDto>> GetAllRepositoriesAsync(string project, CancellationToken ct);

    /// <summary>Distinct .sln file names found under /src in a repository (for the Subsystems column). Empty on any failure.</summary>
    Task<IReadOnlyList<string>> GetSolutionNamesAsync(string project, string repositoryId, CancellationToken ct);

    /// <summary>Commits on a branch authored on/after <paramref name="since"/> (branch history since the window start).</summary>
    Task<IReadOnlyList<AdoGitCommitDto>> GetCommitsOnBranchSinceAsync(string project, string repositoryId, string branch, DateTimeOffset since, int top, CancellationToken ct);

    /// <summary>Pull requests targeting a branch (all statuses, newest first); filter by creation date at the call site.</summary>
    Task<IReadOnlyList<AdoPullRequestDto>> GetPullRequestsTargetingBranchAsync(string project, string repositoryId, string branch, int top, CancellationToken ct);

    /// <summary>Work item ids linked to a pull request.</summary>
    Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string project, string repositoryId, int pullRequestId, CancellationToken ct);
}

public sealed class GitQueries : IGitQueries
{
    private readonly AdoClient _client;

    public GitQueries(AdoClient client) => _client = client;

    public Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(
        string repositoryId, string commitId, CancellationToken ct) =>
        GetCommitChangesCoreAsync(_client.ProjectApiPath($"git/repositories/{repositoryId}/commits/{commitId}/changes?api-version=7.1"), ct);

    public Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(
        string project, string repositoryId, string commitId, CancellationToken ct) =>
        GetCommitChangesCoreAsync(_client.ProjectApiPath(project, $"git/repositories/{repositoryId}/commits/{commitId}/changes?api-version=7.1"), ct);

    private async Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesCoreAsync(string path, CancellationToken ct)
    {
        var result = await _client.GetAsync<AdoCommitChangesDto>(path, ct);
        return result.Changes
            .Where(c => c.Item?.Path is not null)
            .Select(c => (c.Item!.Path!, c.ChangeType ?? "edit"))
            .ToList();
    }

    public Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string name, CancellationToken ct) =>
        FindRepositoryCoreAsync(_client.ProjectApiPath("git/repositories?api-version=7.1"), name, ct);

    public Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string project, string name, CancellationToken ct) =>
        FindRepositoryCoreAsync(_client.ProjectApiPath(project, "git/repositories?api-version=7.1"), name, ct);

    private async Task<AdoRepositoryDto?> FindRepositoryCoreAsync(string path, string name, CancellationToken ct)
    {
        var result = await _client.GetAsync<AdoListResponse<AdoRepositoryDto>>(path, ct);
        return result.Value.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<AdoGitCommitDto>> GetCommitsOnBranchSinceAsync(
        string project, string repositoryId, string branch, DateTimeOffset since, int top, CancellationToken ct)
    {
        var fromDate = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var path = _client.ProjectApiPath(project,
            $"git/repositories/{repositoryId}/commits?searchCriteria.itemVersion.versionType=branch" +
            $"&searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}" +
            $"&searchCriteria.fromDate={Uri.EscapeDataString(fromDate)}&searchCriteria.$top={top}&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoGitCommitDto>>(path, ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<AdoPullRequestDto>> GetPullRequestsTargetingBranchAsync(
        string project, string repositoryId, string branch, int top, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project,
            $"git/repositories/{repositoryId}/pullrequests?searchCriteria.targetRefName=refs/heads/{Uri.EscapeDataString(branch)}" +
            $"&searchCriteria.status=all&$top={top}&api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoPullRequestDto>>(path, ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(
        string project, string repositoryId, int pullRequestId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project,
            $"git/repositories/{repositoryId}/pullRequests/{pullRequestId}/workitems?api-version=7.1");
        var result = await _client.GetAsync<AdoListResponse<AdoResourceRefDto>>(path, ct);
        return result.Value.Select(r => int.TryParse(r.Id, out var id) ? id : 0).Where(id => id > 0).ToList();
    }

    public async Task<IReadOnlyList<AdoRepositoryDto>> GetAllRepositoriesAsync(string project, CancellationToken ct)
    {
        var result = await _client.GetAsync<AdoListResponse<AdoRepositoryDto>>(
            _client.ProjectApiPath(project, "git/repositories?api-version=7.1"), ct);
        return result.Value;
    }

    public async Task<IReadOnlyList<string>> GetSolutionNamesAsync(string project, string repositoryId, CancellationToken ct)
    {
        try
        {
            var path = _client.ProjectApiPath(project,
                $"git/repositories/{repositoryId}/items?scopePath=/src&recursionLevel=Full&api-version=7.1");
            var result = await _client.GetAsync<AdoListResponse<AdoGitItemDto>>(path, ct);
            return result.Value
                .Where(i => i.Path is not null && i.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                .Select(i => System.IO.Path.GetFileNameWithoutExtension(i.Path!))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // no /src, empty repo, or transient error — best-effort
        }
    }
}
