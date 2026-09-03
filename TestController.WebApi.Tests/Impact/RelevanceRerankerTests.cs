using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Rerank;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class RelevanceRerankerTests
{
    private const string VariedJson =
        "[{\"id\":1,\"grade\":0,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":2,\"grade\":1,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":3,\"grade\":2,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":4,\"grade\":3,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":5,\"grade\":2,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":6,\"grade\":1,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}]";

    private const string ValidJson123 =
        "[{\"id\":1,\"grade\":3,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":2,\"grade\":2,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}," +
        "{\"id\":3,\"grade\":1,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[\"s\"]}]";

    private static ChangeDocument Change()
        => new("A", [], ["Sym"], ["Some literal here"], ["Api(int) -> void"], "narrative", "raw", "fp123");

    private static HydeQuery Hyde() => new("summary", ["t"], "body", ["term"]);

    private static ChangePayload Payload()
        => new(1, "t", "d", [], [new TestControllerGrpc.Core.Impact.FileDiff("a.cs", [new DiffHunk(1, 1, "+ public void Api(int x)")])], []);

    private static IReadOnlyList<Scored<TestCaseCandidate>> Cands(params int[] ids)
        => ids.Select(id => new Scored<TestCaseCandidate>(
            new TestCaseCandidate(new AdoWorkItemRef(id, "Test Case", $"TC{id}", null, "Design", 1), "steps", "Automated", 900, []),
            0.5, [])).ToList();

    private static LlmRelevanceReranker Reranker(FakeLlm llm, ImpactMappingOptions? options = null)
    {
        ImpactMappingOptions opts = options ?? new ImpactMappingOptions();
        opts.Rerank.LlmModel = "gpt";
        return new LlmRelevanceReranker(llm, Options.Create(opts), new NoopAppLogger());
    }

    [Fact]
    public async Task PassThrough_Should_ReturnGrade2_ForAllCandidates()
    {
        IReadOnlyDictionary<int, RelevanceJudgement> result =
            await new PassThroughReranker().RerankAsync(Change(), Hyde(), Payload(), Cands(1, 2), CancellationToken.None);

        Assert.All(result.Values, j =>
        {
            Assert.Equal(2, j.Grade);
            Assert.Equal(0.5, j.Confidence);
            Assert.Equal("rerank disabled", j.Reason);
        });
    }

    [Fact]
    public async Task Rerank_Should_ReturnGradesSpanningAtLeastThreeValues()
    {
        IReadOnlyDictionary<int, RelevanceJudgement> result =
            await Reranker(new FakeLlm(VariedJson)).RerankAsync(Change(), Hyde(), Payload(), Cands(1, 2, 3, 4, 5, 6), CancellationToken.None);

        int distinctGrades = result.Values.Select(j => j.Grade).Distinct().Count();
        Assert.True(distinctGrades >= 3, $"Expected at least 3 distinct grades, saw {distinctGrades}.");
    }

    [Fact]
    public async Task Rerank_Should_FailOpen_When_ResponseCorrupt()
    {
        IReadOnlyDictionary<int, RelevanceJudgement> result =
            await Reranker(new FakeLlm("garbage not json")).RerankAsync(Change(), Hyde(), Payload(), Cands(1, 2, 3), CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.All(result.Values, j =>
        {
            Assert.Equal(2, j.Grade);
            Assert.Equal("rerank unavailable", j.Reason);
        });
    }

    [Fact]
    public async Task Rerank_Should_IssueNoLlmCalls_OnSecondRun_ForSameChange()
    {
        var llm = new FakeLlm(ValidJson123);
        LlmRelevanceReranker reranker = Reranker(llm);

        await reranker.RerankAsync(Change(), Hyde(), Payload(), Cands(1, 2, 3), CancellationToken.None);
        int callsAfterFirst = llm.Calls;
        await reranker.RerankAsync(Change(), Hyde(), Payload(), Cands(1, 2, 3), CancellationToken.None);

        Assert.True(callsAfterFirst >= 1);
        Assert.Equal(callsAfterFirst, llm.Calls);
    }

    [Fact]
    public async Task Rerank_Should_Downgrade_When_NoCitedSignals()
    {
        const string noCite = "[{\"id\":1,\"grade\":3,\"confidence\":0.9,\"reason\":\"r\",\"citedSignals\":[]}]";

        IReadOnlyDictionary<int, RelevanceJudgement> result =
            await Reranker(new FakeLlm(noCite)).RerankAsync(Change(), Hyde(), Payload(), Cands(1), CancellationToken.None);

        Assert.Equal(2, result[1].Grade);
    }

    private sealed class FakeLlm(Func<LlmRequest, string?> responder) : ILlmClient
    {
        public FakeLlm(string? response) : this(_ => response) { }

        public int Calls { get; private set; }

        public Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
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
