using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Unit tests for the LLM map→reduce change summarizer. Uses hand-rolled fakes for
/// <see cref="ILlmClient"/> / <see cref="IChangeDiffSource"/> (no mock lib / no network),
/// and verifies both the model-backed path and the deterministic offline fallback.
/// </summary>
public class LlmChurnSummarizerTests
{
    private const string Sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    [Fact]
    public async Task SummarizeComponentAsync_Should_ReturnModelSummary_When_LlmReturnsJson()
    {
        var sut = Build(_ => "{\"summary\":\"AI component summary\"}");

        var result = await sut.SummarizeComponentAsync(Row("Alpha", Change(Sha, "fix login")), default);

        Assert.Equal("AI component summary", result);
    }

    [Fact]
    public async Task SummarizeComponentAsync_Should_FallBackToOffline_When_LlmReturnsNull()
    {
        var sut = Build(_ => null);

        var result = await sut.SummarizeComponentAsync(Row("Alpha", Change(Sha, "fix login")), default);

        Assert.Contains("Alpha", result); // deterministic offline summary names the component
    }

    [Fact]
    public async Task SummarizeReleaseAsync_Should_ReturnHeadlineAndHighlights_When_LlmReturnsJson()
    {
        var sut = Build(req => req.Model == "reduce"
            ? "{\"headline\":\"Big release\",\"highlights\":[\"a\",\"b\"],\"narrative\":\"regress Alpha first\"}"
            : "{\"summary\":\"component changed\"}");

        var summary = await sut.SummarizeReleaseAsync(Report(Row("Alpha", Change(Sha, "fix"))), default);

        Assert.Equal("Big release", summary.Headline);
        Assert.Contains("a", summary.Highlights);
        Assert.Equal("regress Alpha first", summary.Narrative);
    }

    [Fact]
    public async Task SummarizeReleaseAsync_Should_FallBackToOffline_When_LlmReturnsNull()
    {
        var sut = Build(_ => null);

        var summary = await sut.SummarizeReleaseAsync(Report(Row("Alpha", Change(Sha, "fix"))), default);

        Assert.Contains("component(s)", summary.Headline); // deterministic offline headline
    }

    // ── Fixtures ────────────────────────────────────────────────────────

    private static LlmChurnSummarizer Build(Func<LlmRequest, string?> responder) => new(
        new FakeDiffSource(),
        new FakeLlmClient(responder),
        new ChurnSummarizer(),
        Options.Create(new LlmOptions { Enabled = true, MapModel = "map", ReduceModel = "reduce" }),
        Options.Create(new AdoOptions { OmiProject = "AppServer OMI", Project = "System Platform" }),
        new NoopAppLogger());

    private static SubsystemRow Row(string component, params RegressionChangeRef[] changes) => new(
        Component: component,
        Subsystem: component,
        Category: RegressionCategoryKind.Runtime,
        CategoryConfidence: RegressionEvidenceKind.Declared,
        FilesModified: [],
        TotalFilesModified: changes.Sum(c => c.FilePaths.Count),
        Changes: changes,
        RiskTier: "succeeded",
        AutomatedSuites: [],
        ManualSuites: [],
        EstimatedMinutes: 0,
        IsEstimate: false,
        Repository: "AppServer.AAMXCore");

    private static RegressionChangeRef Change(string id, string summary) => new(
        ChangeId: id,
        Summary: summary,
        ObservedUtc: DateTimeOffset.UtcNow,
        FilePaths: ["src/Foo.cs"],
        WorkItems: [],
        Kind: RegressionChangeKind.Commit);

    private static ChurnReport Report(params SubsystemRow[] rows) => new(
        ScopeLabel: "Build",
        RangeText: "2026-01-01 - 2026-01-07",
        From: new DateOnly(2026, 1, 1),
        To: new DateOnly(2026, 1, 7),
        GeneratedUtc: DateTimeOffset.UtcNow,
        Rows: rows);

    private sealed class FakeLlmClient(Func<LlmRequest, string?> responder) : ILlmClient
    {
        public Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeDiffSource : IChangeDiffSource
    {
        public Task<CommitDiff> GetCommitDiffAsync(string project, string repositoryId, string commitId, CancellationToken ct) =>
            Task.FromResult(new CommitDiff(commitId, []));
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
