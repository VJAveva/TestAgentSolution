using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Pins the branch-switcher filtering rules. A production config of <c>["".]</c> (one blank entry) is NOT
/// the same as <c>[]</c>: <see cref="GlobUtil.IsMatch"/> rejects a blank glob, so an unguarded filter
/// silently dropped every live branch and left the picker empty on both hosts.
/// </summary>
public class RegressionSourceCatalogBranchTests
{
    private static readonly string[] LiveBranches =
        ["Dev", "main", "releases/SP2026", "Releases/SP2023R2/2023R2SP2", "users/someone/wip"];

    private static RegressionSourceCatalog Build(params string[] includePatterns)
    {
        var options = new AdoOptions
        {
            Enabled = true,
            Organization = "AVEVA-VSTS",
            Project = "System Platform",
            OmiProject = "AppServer OMI",
            Branches = [],
            BranchIncludePatterns = [.. includePatterns],
        };

        return new RegressionSourceCatalog(
            Options.Create(options),
            new EmptyComponentMap(),
            new SingleServiceProvider(typeof(IBuildQueries), new BranchOnlyBuildQueries(LiveBranches)));
    }

    [Fact]
    public async Task GetBranchesAsync_Should_ReturnAllLiveBranches_When_PatternListIsEmpty()
    {
        var branches = await Build().GetBranchesAsync(CancellationToken.None);
        Assert.Equal(LiveBranches.Length, branches.Count);
    }

    [Fact]
    public async Task GetBranchesAsync_Should_ReturnAllLiveBranches_When_OnlyPatternIsBlank()
    {
        // The production regression: [""] has Count == 1, so the filtering path ran and an empty
        // glob matched nothing, emptying the switcher.
        var branches = await Build("").GetBranchesAsync(CancellationToken.None);
        Assert.Equal(LiveBranches.Length, branches.Count);
    }

    [Fact]
    public async Task GetBranchesAsync_Should_IgnoreBlankEntries_When_MixedWithRealPatterns()
    {
        var branches = await Build("", "releases/*").GetBranchesAsync(CancellationToken.None);
        Assert.Equal(["Releases/SP2023R2/2023R2SP2", "releases/SP2026"], branches);
    }

    [Fact]
    public async Task GetBranchesAsync_Should_FilterToMatchingBranches_When_PatternIsSet()
    {
        var branches = await Build("releases/*").GetBranchesAsync(CancellationToken.None);
        Assert.DoesNotContain("Dev", branches);
        Assert.DoesNotContain("users/someone/wip", branches);
    }

    private sealed class EmptyComponentMap : IComponentBuildMap
    {
        public ComponentBuildInfo? Resolve(string manifestComponentName) => null;
        public IReadOnlyList<ComponentBuildInfo> All() => [];
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type t) => t == serviceType ? instance : null;
    }

    /// <summary>Only <see cref="GetRecentSourceBranchesAsync"/> is exercised by the branch switcher.</summary>
    private sealed class BranchOnlyBuildQueries(IReadOnlyList<string> branches) : IBuildQueries
    {
        public Task<IReadOnlyList<string>> GetRecentSourceBranchesAsync(string project, int top, CancellationToken ct)
            => Task.FromResult(branches);

        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsAsync(DateOnly from, DateOnly to, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(int definitionId, int top, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, string? branchName, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AdoBuildDto?> GetFirstBuildOnBranchAsync(string project, int definitionId, string branchName, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, string? branchName, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDefinitionDto>> GetBuildDefinitionsAsync(string project, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AdoBuildDto?> GetBuildAsync(string project, int buildId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(int buildId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(string project, int buildId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(int buildId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(string project, int buildId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
