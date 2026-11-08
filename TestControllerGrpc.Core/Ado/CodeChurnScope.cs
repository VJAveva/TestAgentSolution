using Microsoft.Extensions.Options;
using TestControllerGrpc.Features;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Which components, files and branches Code Churn covers. When Features.json has no "CodeChurn:Repositories"
/// section everything in the component map is in scope, exactly as before.
/// </summary>
public interface ICodeChurnScope
{
    /// <summary>True when Features.json lists repositories (even an empty list).</summary>
    bool IsConfigured { get; }
    IReadOnlyList<CodeChurnRepository> Repositories { get; }
    string Fingerprint { get; }
    CodeChurnRepository? Find(string? repository);

    /// <summary>Component is scanned: its repository is configured, and matches <paramref name="repository"/> when given.</summary>
    bool IncludesComponent(ComponentBuildInfo component, string? repository = null);

    /// <summary>A changed file counts for the repository (Include/ExcludePaths). Always true when not configured.</summary>
    bool InScope(string? repository, string path);
}

public sealed class CodeChurnScope(IFeatureGate? gate = null) : ICodeChurnScope
{
    public bool IsConfigured => gate?.CodeChurnRepositories is not null;
    public IReadOnlyList<CodeChurnRepository> Repositories => gate?.CodeChurnRepositories ?? [];
    public string Fingerprint => CodeChurnRepositoryRules.Fingerprint(gate?.CodeChurnRepositories);

    public CodeChurnRepository? Find(string? repository) =>
        string.IsNullOrWhiteSpace(repository)
            ? null
            : Repositories.FirstOrDefault(r => r.Name.Equals(repository, StringComparison.OrdinalIgnoreCase));

    public bool IncludesComponent(ComponentBuildInfo component, string? repository = null)
    {
        if (!string.IsNullOrWhiteSpace(repository)
            && !string.Equals(component.Repository, repository, StringComparison.OrdinalIgnoreCase))
            return false;
        return !IsConfigured || Find(component.Repository) is not null;
    }

    public bool InScope(string? repository, string path) =>
        Find(repository) is not { } repo || CodeChurnRepositoryRules.InScope(repo, path);
}

/// <summary>One configured repository with everything that will stop it producing churn, in plain words.</summary>
public sealed record CodeChurnRepositoryStatus(
    string Name,
    string? Url,
    IReadOnlyList<string> Branches,
    string? CredentialRef,
    bool CredentialSet,
    IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Checks each configured repository against the component map and Azure DevOps: unreachable or unknown repo,
/// branches that do not exist, a Url outside the organization/project Code Churn reads, an unset credential.
/// Never reads a credential value beyond "is it set".
/// </summary>
public sealed class CodeChurnRepositoryValidator(
    ICodeChurnScope scope,
    IComponentBuildMap map,
    IOptions<AdoOptions> options,
    IServiceProvider services,
    IAppLogger? logger = null)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, DateTimeOffset At, IReadOnlyList<CodeChurnRepositoryStatus> Result)? _cache;

    public async Task<IReadOnlyList<CodeChurnRepositoryStatus>> ValidateAsync(bool refresh, CancellationToken ct)
    {
        if (!scope.IsConfigured) return [];

        var key = scope.Fingerprint;
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _cache is { } c && c.Key == key && DateTimeOffset.UtcNow - c.At < CacheFor)
                return c.Result;

            var result = await ValidateCoreAsync(ct);
            _cache = (key, DateTimeOffset.UtcNow, result);
            foreach (var status in result)
                foreach (var problem in status.Problems)
                    logger?.Warn("CodeChurn", $"Repository '{status.Name}': {problem}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CodeChurnRepositoryStatus>> ValidateCoreAsync(CancellationToken ct)
    {
        var ado = options.Value;
        var project = string.IsNullOrWhiteSpace(ado.OmiProject) ? ado.Project : ado.OmiProject;
        var components = map.All();

        Dictionary<string, Dto.AdoRepositoryDto>? adoRepos = null;
        string? adoError = null;
        var git = ado.Enabled ? services.GetService(typeof(IGitQueries)) as IGitQueries : null;
        if (git is not null)
        {
            try
            {
                adoRepos = (await git.GetAllRepositoriesAsync(project, ct))
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                adoError = $"Azure DevOps could not be reached to check it ({ex.Message}).";
            }
        }

        var result = new List<CodeChurnRepositoryStatus>();
        foreach (var repo in scope.Repositories)
        {
            var problems = new List<string>();

            if (UrlProblem(repo.Url, ado.Organization, project) is { } urlProblem)
                problems.Add(urlProblem);

            if (!components.Any(c => string.Equals(c.Repository, repo.Name, StringComparison.OrdinalIgnoreCase)))
                problems.Add("No component in the component map uses this repository, so nothing is scanned for it.");

            if (repo.Branches.Count == 0)
                problems.Add("No branches are listed for this repository.");

            var credentialSet = string.IsNullOrWhiteSpace(repo.CredentialRef) || IsSet(repo.CredentialRef);
            if (!credentialSet)
                problems.Add($"Credential '{repo.CredentialRef}' is not set on this controller.");

            if (adoError is not null)
            {
                problems.Add(adoError);
            }
            else if (adoRepos is not null)
            {
                if (!adoRepos.TryGetValue(repo.Name, out var found))
                {
                    problems.Add($"Repository not found in Azure DevOps project '{project}'.");
                }
                else if (repo.Branches.Count > 0)
                {
                    try
                    {
                        var existing = await git!.GetBranchNamesAsync(project, found.Id, ct);
                        if (existing is not null)
                        {
                            var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                            foreach (var branch in repo.Branches.Where(b => !set.Contains(Normalize(b))))
                                problems.Add($"Branch '{branch}' does not exist in this repository.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        problems.Add($"Branches could not be checked ({ex.Message}).");
                    }
                }
            }

            result.Add(new CodeChurnRepositoryStatus(repo.Name, repo.Url, repo.Branches, repo.CredentialRef, credentialSet, problems));
        }
        return result;
    }

    private static string Normalize(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) ? branch["refs/heads/".Length..] : branch;

    private static bool IsSet(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine));

    /// <summary>Code Churn reads one organization and project; a Url anywhere else would silently scan nothing.</summary>
    internal static string? UrlProblem(string? url, string organization, string project)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        string? org, proj;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            org = segments.ElementAtOrDefault(0);
            proj = segments.ElementAtOrDefault(1);
        }
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            org = uri.Host[..uri.Host.IndexOf('.')];
            proj = segments.ElementAtOrDefault(0);
        }
        else
        {
            return "Url is not an Azure DevOps address; Code Churn reads Azure DevOps only.";
        }

        if (!string.Equals(org, organization, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(proj, project, StringComparison.OrdinalIgnoreCase))
            return $"Url points at '{org}/{proj}', but Code Churn reads '{organization}/{project}' (Ado:Organization / Ado:OmiProject).";
        return null;
    }
}
