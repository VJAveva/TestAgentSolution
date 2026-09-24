using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Churn baselining. The branch scan used to start at the branch's FIRST build, which on a long-lived branch
/// such as Dev is years back — the build's own delta was buried under its entire history. These cover the
/// replacement baseline (previous successful build on the same branch), its bounded fallback, the branch
/// scoping the baseline depends on, and the paging that stops changes being dropped past the first page.
/// </summary>
public class ChurnBaselineTests
{
    // ---- window resolution --------------------------------------------------

    private static AdoBuildDto Build(int id, DateTimeOffset? finish, DateTimeOffset? start = null) =>
        new() { Id = id, FinishTime = finish, StartTime = start ?? finish };

    [Fact]
    public void ResolveBranchWindowStart_Should_UsePreviousBuildFinish_When_ABuildSucceededOnTheBranch()
    {
        var now = new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.Zero);
        var previous = Build(2, now.AddDays(-2));
        var first = Build(1, now.AddYears(-2));

        var since = ComponentChangeCollector.ResolveBranchWindowStart(previous, first, now);

        Assert.Equal(now.AddDays(-2), since);
    }

    [Fact]
    public void ResolveBranchWindowStart_Should_CapAtThirtyDays_When_NoPreviousSuccessfulBuildExists()
    {
        var now = new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.Zero);
        var first = Build(1, now.AddYears(-2));   // branch first built in 2024

        var since = ComponentChangeCollector.ResolveBranchWindowStart(null, first, now);

        Assert.Equal(now.AddDays(-30), since);
    }

    [Fact]
    public void ResolveBranchWindowStart_Should_FloorAtFirstBuild_When_TheBranchIsYoungerThanTheFallback()
    {
        var now = new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.Zero);
        var first = Build(1, now.AddDays(-3));    // branch only 3 days old

        var since = ComponentChangeCollector.ResolveBranchWindowStart(null, first, now);

        // Never claim changes from before the branch existed.
        Assert.Equal(now.AddDays(-3), since);
    }

    // ---- branch scoping and paging -----------------------------------------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Body, string? Continuation)> _script;
        public List<string> Requests { get; } = [];

        public ScriptedHandler(Func<string, (HttpStatusCode, string, string?)> script) => _script = script;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            var (status, body, continuation) = _script(uri);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            if (continuation is not null)
                response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", continuation);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeToken : IAdoTokenProvider
    {
        public Task<string> GetAuthHeaderAsync(CancellationToken ct) => Task.FromResult("Basic test");
        public string Describe() => "test";
    }

    private sealed class NoopLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

    private static BuildQueries Queries(ScriptedHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/org/") };
        var options = Options.Create(new AdoOptions { Organization = "org", Project = "proj" });
        return new BuildQueries(new AdoClient(http, new FakeToken(), options, new NoopLogger()));
    }

    [Fact]
    public async Task GetPreviousBuildAsync_Should_IgnoreOtherBranches_When_ABranchIsGiven()
    {
        // The unscoped query would answer with a 2024 build from another branch; the scoped one must not see it.
        const string otherBranchBuild = """{"count":1,"value":[{"id":11,"finishTime":"2024-10-04T10:00:00Z"}]}""";
        const string devBuild = """{"count":1,"value":[{"id":22,"finishTime":"2026-09-21T10:00:00Z"}]}""";

        var handler = new ScriptedHandler(uri =>
            uri.Contains("branchName=refs/heads/Dev", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode.OK, devBuild, null)
                : (HttpStatusCode.OK, otherBranchBuild, null));

        var result = await Queries(handler).GetPreviousBuildAsync(
            "proj", definitionId: 7, before: new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
            branchName: "Dev", succeededOnly: true, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(22, result!.Id);
        Assert.Contains(handler.Requests, r => r.Contains("branchName=refs/heads/Dev", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, r => r.Contains("resultFilter=succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetBuildChangesAsync_Should_FollowContinuation_When_ChangesSpanMoreThanOnePage()
    {
        const string page1 = """{"count":2,"value":[{"id":"c1"},{"id":"c2"}]}""";
        const string page2 = """{"count":1,"value":[{"id":"c3"}]}""";

        var handler = new ScriptedHandler(uri =>
            uri.Contains("continuationToken=tok2", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode.OK, page2, null)      // last page: no continuation header
                : (HttpStatusCode.OK, page1, "tok2"));

        var changes = await Queries(handler).GetBuildChangesAsync("proj", buildId: 99, CancellationToken.None);

        Assert.Equal(3, changes.Count);
        Assert.Equal(["c1", "c2", "c3"], changes.Select(c => c.Id).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("$top=200", r));
    }

    [Fact]
    public async Task GetBuildChangesAsync_Should_StopAtOnePage_When_NoContinuationIsReturned()
    {
        var handler = new ScriptedHandler(_ => (HttpStatusCode.OK, """{"count":1,"value":[{"id":"only"}]}""", null));

        var changes = await Queries(handler).GetBuildChangesAsync("proj", buildId: 99, CancellationToken.None);

        Assert.Single(changes);
        Assert.Single(handler.Requests);
    }

    // ---- legacy SP-anchored baseline ---------------------------------------

    private static AdoBuildDto OnBranch(int id, string? branch) =>
        new() { Id = id, SourceBranch = branch, FinishTime = DateTimeOffset.UtcNow.AddDays(-id) };

    [Fact]
    public void FilterToNewestBranch_Should_DropOtherBranches_When_TheDefinitionBuildsSeveral()
    {
        var builds = new[]
        {
            OnBranch(1, "refs/heads/Dev"),
            OnBranch(2, "refs/heads/SP2023R2SP2"),   // another branch — would diff across the divergence
            OnBranch(3, "refs/heads/Dev"),
        };

        var kept = SpBuildImpactCollector.FilterToNewestBranch(builds);

        Assert.Equal([1, 3], kept.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void FilterToNewestBranch_Should_KeepEverything_When_BuildsCarryNoBranchInformation()
    {
        var builds = new[] { OnBranch(1, null), OnBranch(2, null) };

        var kept = SpBuildImpactCollector.FilterToNewestBranch(builds);

        // Without branch data, discarding would leave no baseline at all — legacy behaviour is safer.
        Assert.Equal(2, kept.Count);
    }

    [Theory]
    [InlineData("refs/heads/Dev", "Dev")]
    [InlineData("refs/heads/feature/a-b", "feature/a-b")]
    [InlineData(null, null)]
    public void ShortBranch_Should_StripTheRefPrefix(string? input, string? expected)
        => Assert.Equal(expected, SpBuildImpactCollector.ShortBranch(input));
}
