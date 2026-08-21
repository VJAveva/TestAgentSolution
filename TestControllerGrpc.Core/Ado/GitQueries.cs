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
