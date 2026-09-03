using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Query;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class HydeQueryGeneratorTests
{
    private const string ValidJson =
        "{\"changeSummary\":\"The login flow changed.\"," +
        "\"syntheticTestTitles\":[\"User logs in successfully\",\"Invalid password rejected\"]," +
        "\"syntheticTestBody\":\"Open the app, enter credentials, verify access.\"," +
        "\"expandedTerms\":[\"authentication\",\"credentials\"]}";

    private static ChangeDocument Doc() => new(
        "AREA-1", PathTokens: ["deploy", "galaxy"], ChangedSymbols: ["DeployGalaxy", "RunDeployment"],
        ChangedLiterals: ["User can log in", "Session expires after timeout"],
        PublicApiChanges: ["DeployGalaxy(string) -> Task"], PrNarrative: null, RawText: "raw", Fingerprint: "fp");

    private static ImpactMappingOptions Options(string model = "gpt-4o", bool enableHyde = true)
    {
        var options = new ImpactMappingOptions();
        options.Rerank.LlmModel = model;
        options.Rerank.EnableHyde = enableHyde;
        return options;
    }

    private static HydeQueryGenerator Generator(FakeLlmClient llm, ImpactMappingOptions options)
        => new(llm, Microsoft.Extensions.Options.Options.Create(options), new NoopAppLogger());

    [Fact]
    public async Task GenerateAsync_Should_ParseLlmJson_When_ModelConfigured()
    {
        var llm = new FakeLlmClient(ValidJson);

        HydeQuery result = await Generator(llm, Options()).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal("The login flow changed.", result.ChangeSummary);
        Assert.Contains("User logs in successfully", result.SyntheticTestTitles);
        Assert.Contains("authentication", result.ExpandedTerms);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task GenerateAsync_Should_StripFences_BeforeParsing()
    {
        var llm = new FakeLlmClient("```json\n" + ValidJson + "\n```");

        HydeQuery result = await Generator(llm, Options()).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal("The login flow changed.", result.ChangeSummary);
    }

    [Fact]
    public async Task GenerateAsync_Should_RetryWithStricter_Then_Succeed()
    {
        var llm = new FakeLlmClient("not json at all", ValidJson);

        HydeQuery result = await Generator(llm, Options()).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal("The login flow changed.", result.ChangeSummary);
        Assert.Equal(2, llm.Calls);
    }

    [Fact]
    public async Task GenerateAsync_Should_RetryOnce_Then_Fallback_When_UnparseableTwice()
    {
        var llm = new FakeLlmClient("garbage", "still garbage");

        HydeQuery result = await Generator(llm, Options()).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal(2, llm.Calls);
        Assert.Contains("User can log in", result.SyntheticTestTitles); // deterministic fallback from the doc
    }

    [Fact]
    public async Task GenerateAsync_Should_Fallback_Without_CallingLlm_When_HydeDisabled()
    {
        var llm = new FakeLlmClient(ValidJson);

        HydeQuery result = await Generator(llm, Options(enableHyde: false)).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.Contains("User can log in", result.SyntheticTestTitles);
    }

    [Fact]
    public async Task GenerateAsync_Should_Fallback_Without_CallingLlm_When_ModelEmpty()
    {
        var llm = new FakeLlmClient(ValidJson);

        HydeQuery result = await Generator(llm, Options(model: "")).GenerateAsync(Doc(), CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.Contains("User can log in", result.SyntheticTestTitles);
    }

    private sealed class FakeLlmClient(params string?[] responses) : ILlmClient
    {
        private readonly Queue<string?> _responses = new(responses);

        public int Calls { get; private set; }

        public Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
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
