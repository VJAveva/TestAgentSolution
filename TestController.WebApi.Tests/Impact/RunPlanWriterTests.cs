using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Execution;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class RunPlanWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"runplan-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static RunPlanWriter Writer(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()), new NoopAppLogger());

    private static MappedTestCase Tc(int id, string? automatedName, string status = "Automated")
        => new(
            new TestCaseCandidate(
                new AdoWorkItemRef(id, "Test Case", $"TC{id}", null, "Design", 1),
                "steps", status, 900, [], automatedName),
            900, 0.8, MappingConfidence.Observed, null, null, []);

    private static ImpactMappingResult Result(params MappedTestCase[] cases)
        => new(
            new ImpactedArea("AAMxCore", "AAMxCore", "Core", null, [], [], RiskTier.High,
                new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch)),
            [], [], cases, [], new AnchorResult([], 0, false),
            new SelectionDiagnostics(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, null),
            SelectionTier.Targeted, EarlyExit: false, [], TimeSpan.Zero,
            Guid.Parse("3f2a1b4c-5d6e-4f80-9a1b-2c3d4e5f6071"));

    private static PipelineParameterConfig Read(string path)
        => JsonSerializer.Deserialize<PipelineParameterConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task WriteAsync_Should_ReturnNull_When_NoCaseIsAutomated()
    {
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, null, "Not Automated")), _folder, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_Should_ReturnNull_When_AutomatedCaseHasNoTestName()
    {
        // Status says Automated but the join key is absent — there is nothing to put in a filter.
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, null)), _folder, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_Should_WriteManifestReadableByParameterResolver()
    {
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, "Galaxy.Deploy.Smoke"), Tc(102, "Galaxy.Deploy.Rollback")),
            _folder, CancellationToken.None);

        Assert.NotNull(result);
        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadTriggerFile(ctx, result.ManifestPath);

        Assert.Equal("3f2a1b4c-5d6e-4f80-9a1b-2c3d4e5f6071", ctx.Parameters["_ImpactRunId"]);
        Assert.Equal("101,102", ctx.Parameters["_TestCaseIds"]);
        Assert.Equal("High", ctx.Parameters["_ImpactRiskTier"]);
        Assert.Equal("2", ctx.Parameters["_AutomatedCount"]);
    }

    [Fact]
    public async Task WriteAsync_Should_ApplyTriggerRank_When_ManifestIsLoaded()
    {
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, "Galaxy.Deploy.Smoke")), _folder, CancellationToken.None);

        var ctx = new PipelineExecutionContext();
        ParameterResolver.SetParameter(ctx, "_TestCaseIds", "999", ParameterRank.ParameterFile);
        ParameterResolver.LoadTriggerFile(ctx, result!.ManifestPath);

        // A trigger outranks a parameter file; if this regressed the manifest would be ignored.
        Assert.Equal("101", ctx.Parameters["_TestCaseIds"]);
    }

    [Fact]
    public async Task WriteAsync_Should_LeaveNoTempFile()
    {
        await Writer().WriteAsync(Result(Tc(101, "Galaxy.Deploy.Smoke")), _folder, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_Should_SeparateManualCases_When_SelectionIsMixed()
    {
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, "Galaxy.Deploy.Smoke"), Tc(102, null, "Not Automated")),
            _folder, CancellationToken.None);

        Assert.Equal(1, result!.AutomatedCount);
        Assert.Equal(1, result.ManualCount);

        PipelineParameterConfig config = Read(result.ManifestPath);
        Assert.Equal("101", config.Global["_TestCaseIds"]);
        Assert.Equal("102", config.Global["_ManualTestCaseIds"]);
        Assert.DoesNotContain("102", config.Global["_TestFilter"]);
    }

    [Fact]
    public async Task WriteAsync_Should_WriteTestListFile_ContainingEveryAutomatedName()
    {
        RunPlanResult? result = await Writer().WriteAsync(
            Result(Tc(101, "Galaxy.Deploy.Smoke"), Tc(102, "Galaxy.Deploy.Rollback")),
            _folder, CancellationToken.None);

        string[] lines = await File.ReadAllLinesAsync(result!.TestListPath);

        Assert.Equal(["Galaxy.Deploy.Rollback", "Galaxy.Deploy.Smoke"], lines);
    }

    [Fact]
    public async Task WriteAsync_Should_OmitInlineFilter_When_ItExceedsTheLimit()
    {
        var options = new ImpactMappingOptions();
        options.Selection.MaxInlineFilterChars = 10;

        RunPlanResult? result = await Writer(options).WriteAsync(
            Result(Tc(101, "Galaxy.Deploy.Smoke"), Tc(102, "Galaxy.Deploy.Rollback")),
            _folder, CancellationToken.None);

        Assert.False(result!.FilterInlined);
        PipelineParameterConfig config = Read(result.ManifestPath);
        Assert.False(config.Global.ContainsKey("_TestFilter"));
        Assert.True(config.Global.ContainsKey("_TestListFile"));
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
