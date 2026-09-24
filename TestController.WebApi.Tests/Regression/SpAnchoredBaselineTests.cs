using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Exercises the legacy SP-anchored collector end to end with <c>IsSpAnchored = true</c>. Production runs
/// Components mode, so this path has no other coverage — without it the branch-scoped baseline could rot
/// unnoticed until someone flips the mode. The discriminating case is a window whose builds span two
/// branches: unfiltered, the baseline is the other branch's build and the diff crosses the divergence.
/// </summary>
public class SpAnchoredBaselineTests
{
    private const int SpDefinitionId = 42;

    private sealed class RecordingBuildQueries : IBuildQueries
    {
        private readonly IReadOnlyList<AdoBuildDto> _inRange;
        private readonly AdoBuildDto? _scopedPrevious;

        public RecordingBuildQueries(IReadOnlyList<AdoBuildDto> inRange, AdoBuildDto? scopedPrevious)
        {
            _inRange = inRange;
            _scopedPrevious = scopedPrevious;
        }

        public List<(string? Branch, bool SucceededOnly)> PreviousBuildCalls { get; } = [];

        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, CancellationToken ct)
            => Task.FromResult(_inRange);

        public Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, string? branchName, bool succeededOnly, CancellationToken ct)
        {
            PreviousBuildCalls.Add((branchName, succeededOnly));
            return Task.FromResult(_scopedPrevious);
        }

        public Task<AdoBuildDto?> GetPreviousBuildAsync(string project, int definitionId, DateTimeOffset before, CancellationToken ct)
        {
            PreviousBuildCalls.Add((null, false));   // the unscoped overload must NOT be the one used
            return Task.FromResult(_scopedPrevious);
        }

        // ---- unused by this path -------------------------------------------
        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsAsync(DateOnly from, DateOnly to, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(int definitionId, int top, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoBuildDto>>([]);
        public Task<IReadOnlyList<AdoBuildDto>> GetLatestBuildsByDefinitionAsync(string project, int definitionId, int top, string? branchName, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoBuildDto>>([]);
        public Task<AdoBuildDto?> GetFirstBuildOnBranchAsync(string project, int definitionId, string branchName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDto>> GetBuildsByDefinitionInRangeAsync(string project, int definitionId, DateOnly from, DateOnly to, string? branchName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildDefinitionDto>> GetBuildDefinitionsAsync(string project, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRecentSourceBranchesAsync(string project, int top, CancellationToken ct) => throw new NotSupportedException();
        public Task<AdoBuildDto?> GetBuildAsync(string project, int buildId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(int buildId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoBuildChangeDto>>([]);
        public Task<IReadOnlyList<AdoBuildChangeDto>> GetBuildChangesAsync(string project, int buildId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoBuildChangeDto>>([]);
        public Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(int buildId, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<IReadOnlyList<int>> GetBuildWorkItemIdsAsync(string project, int buildId, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
    }

    private sealed class NoRepoGitQueries : IGitQueries
    {
        // Returning null stops section (a) after the baseline decision, which is all this test is about.
        public Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string project, string name, CancellationToken ct) => Task.FromResult<AdoRepositoryDto?>(null);
        public Task<AdoRepositoryDto?> FindRepositoryByNameAsync(string name, CancellationToken ct) => Task.FromResult<AdoRepositoryDto?>(null);
        public Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(string repositoryId, string commitId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Path, string ChangeType)>> GetCommitChangesAsync(string project, string repositoryId, string commitId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoRepositoryDto>> GetAllRepositoriesAsync(string project, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetSolutionNamesAsync(string project, string repositoryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoGitCommitDto>> GetCommitsOnBranchSinceAsync(string project, string repositoryId, string branch, DateTimeOffset since, int top, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdoPullRequestDto>> GetPullRequestsTargetingBranchAsync(string project, string repositoryId, string branch, int top, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string project, string repositoryId, int pullRequestId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class EmptyWorkItems : IWorkItemQueries
    {
        public Task<IReadOnlyList<AdoWorkItemDto>> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoWorkItemDto>>([]);
        public Task<IReadOnlyList<AdoWorkItemDto>> GetWithRelationsAsync(IReadOnlyList<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdoWorkItemDto>>([]);
    }

    private sealed class EmptyManifests : IBuildManifestSource
    {
        public Task<BuildManifest> GetManifestAsync(string project, int buildId, int logId, CancellationToken ct)
            => Task.FromResult(new BuildManifest(buildId, new Dictionary<string, string>()));
    }

    private sealed class EmptyMap : IComponentBuildMap
    {
        public ComponentBuildInfo? Resolve(string manifestComponentName) => null;
        public IReadOnlyList<ComponentBuildInfo> All() => [];
    }

    private sealed class NoResolver : IRepositoryResolver
    {
        public string? ResolveComponent(string pathHint) => null;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(LogLevel level, string category, string message, Exception? ex = null) => Messages.Add(message);
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) => Messages.Add(message);
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) => Messages.Add(message);
        public void Info(string category, string message) => Messages.Add(message);
        public void Warn(string category, string message) => Messages.Add(message);
        public void Error(string category, string message, Exception? ex = null) => Messages.Add(message);
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

    private static AdoBuildDto Build(int id, string branch, DateTimeOffset finish) =>
        new() { Id = id, SourceBranch = branch, FinishTime = finish, StartTime = finish, Result = "succeeded" };

    private static (SpBuildImpactCollector Collector, RecordingBuildQueries Builds, RecordingLogger Log) Subject(
        IReadOnlyList<AdoBuildDto> inRange, AdoBuildDto? scopedPrevious)
    {
        var builds = new RecordingBuildQueries(inRange, scopedPrevious);
        var log = new RecordingLogger();
        var options = Options.Create(new AdoOptions
        {
            Organization = "org",
            Project = "proj",
            Repository = "repo",
            SpBuildDefinitionId = SpDefinitionId,   // this is what makes IsSpAnchored true
        });

        var collector = new SpBuildImpactCollector(
            builds,
            new NoRepoGitQueries(),
            new EmptyWorkItems(),
            new EmptyManifests(),
            new EmptyMap(),
            new AdoChangeTranslator(new NoResolver()),
            options,
            log);

        return (collector, builds, log);
    }

    [Fact]
    public void Options_Should_ReportSpAnchored_When_ADefinitionIdIsConfigured()
    {
        var options = new AdoOptions { SpBuildDefinitionId = SpDefinitionId };

        Assert.True(options.IsSpAnchored);
    }

    [Fact]
    public async Task CollectAsync_Should_NotBaselineOnAnotherBranch_When_TheWindowSpansTwoBranches()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        // Newest first, as ADO returns them. Only build 100 is on Dev, so after filtering the window holds a
        // single build and the baseline must come from the branch-scoped lookup rather than build 200.
        var inRange = new[]
        {
            Build(100, "refs/heads/Dev", now.AddHours(-1)),
            Build(200, "refs/heads/Release-SP2023R2SP2", now.AddHours(-5)),
        };
        var scopedPrevious = Build(90, "refs/heads/Dev", now.AddDays(-1));

        var (collector, builds, log) = Subject(inRange, scopedPrevious);

        await collector.CollectAsync(
            new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23), CancellationToken.None);

        // The branch-scoped, succeeded-only overload is the one that must be consulted.
        var call = Assert.Single(builds.PreviousBuildCalls);
        Assert.Equal("Dev", call.Branch);
        Assert.True(call.SucceededOnly);

        // And the chosen baseline is the same-branch build, never the Release one.
        Assert.Contains(log.Messages, m => m.Contains("current=100") && m.Contains("previous=90"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("previous=200"));
    }

    [Fact]
    public async Task CollectAsync_Should_KeepBothBuilds_When_TheWindowIsAllOneBranch()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var inRange = new[]
        {
            Build(100, "refs/heads/Dev", now.AddHours(-1)),
            Build(80, "refs/heads/Dev", now.AddHours(-6)),
        };

        var (collector, builds, log) = Subject(inRange, scopedPrevious: null);

        await collector.CollectAsync(
            new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23), CancellationToken.None);

        // Two builds on the branch means the window itself supplies the baseline — no extra lookup needed.
        Assert.Empty(builds.PreviousBuildCalls);
        Assert.Contains(log.Messages, m => m.Contains("current=100") && m.Contains("previous=80"));
    }
}
