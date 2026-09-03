using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class OutcomeStoreTests
{
    private static ImpactedArea Area(string id)
        => new(id, "Name", "Sub", "Vob", [], [], RiskTier.High, new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static MappedTestCase Mapped(int tcId, int featureId, double score, int grade, AnchorSource? anchor = null)
        => new(
            new TestCaseCandidate(new AdoWorkItemRef(tcId, "Test Case", $"TC{tcId}", null, "Design", 1), "steps", "Automated", featureId, []),
            featureId, score, MappingConfidence.Declared, new RelevanceJudgement(grade, 0.9, "r", []), anchor, []);

    private static ImpactMappingResult Result(Guid runId, string areaId, params MappedTestCase[] mapped)
        => new(
            Area(areaId), [], [], mapped, [],
            new AnchorResult([], 0, false),
            new SelectionDiagnostics(TimeSpan.Zero, TimeSpan.FromSeconds(30), 0, 0, 0, null),
            SelectionTier.Targeted, false, [], TimeSpan.Zero, runId);

    private static ExecutionOutcome Outcome(int tcId, string result, double seconds)
        => new() { TestCaseId = tcId, Result = result, DurationSeconds = seconds };

    private static OutcomeStore Store(SqliteConnection connection)
        => new(new Factory(connection), Options.Create(new ImpactMappingOptions()), new NoopAppLogger());

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task GetTrainingPairs_Should_PairFinalScoreWithOutcomeLabel()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);
        Guid runId = Guid.NewGuid();

        await store.RecordRunAsync(Result(runId, "AREA", Mapped(101, 900, 0.9, 3), Mapped(102, 900, 0.5, 1)), CancellationToken.None);
        await store.RecordExecutionAsync(runId, [Outcome(101, "Failed", 12), Outcome(102, "Passed", 5)], CancellationToken.None);

        IReadOnlyList<LabelledScore> pairs = await store.GetTrainingPairsAsync(CancellationToken.None);

        Assert.Contains(pairs, p => p.Score == 0.9 && p.Label == 1);
        Assert.Contains(pairs, p => p.Score == 0.5 && p.Label == 0);
    }

    [Fact]
    public async Task GetFailureRates_Should_ComputeFailedOverTotal()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);
        Guid runId = Guid.NewGuid();

        await store.RecordRunAsync(Result(runId, "AREA", Mapped(101, 900, 0.9, 3), Mapped(102, 900, 0.5, 1)), CancellationToken.None);
        await store.RecordExecutionAsync(runId, [Outcome(101, "Failed", 12), Outcome(102, "Passed", 5)], CancellationToken.None);

        IReadOnlyDictionary<int, double> rates = await store.GetFailureRatesAsync([101, 102], CancellationToken.None);

        Assert.Equal(1.0, rates[101]);
        Assert.Equal(0.0, rates[102]);
    }

    [Fact]
    public async Task GetDurations_Should_ReturnMeanDuration()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);
        Guid runId = Guid.NewGuid();

        await store.RecordRunAsync(Result(runId, "AREA", Mapped(101, 900, 0.9, 3)), CancellationToken.None);
        await store.RecordExecutionAsync(runId, [Outcome(101, "Failed", 12)], CancellationToken.None);

        IReadOnlyDictionary<int, TimeSpan> durations = await store.GetDurationsAsync([101], CancellationToken.None);

        Assert.Equal(12, durations[101].TotalSeconds, 3);
    }

    [Fact]
    public async Task GetHistoricalAnchors_Should_ReturnFailedTestCases_WithWeight()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);
        Guid runId = Guid.NewGuid();

        await store.RecordRunAsync(Result(runId, "AREA", Mapped(101, 900, 0.9, 3)), CancellationToken.None);
        await store.RecordExecutionAsync(runId, [Outcome(101, "Failed", 12)], CancellationToken.None);

        IReadOnlyList<AnchorEdge> anchors = await store.GetHistoricalAnchorsAsync("AREA", 6, CancellationToken.None);

        AnchorEdge edge = Assert.Single(anchors);
        Assert.Equal(101, edge.TestCaseId);
        Assert.Equal(AnchorSource.HistoricalFailure, edge.Source);
        Assert.Equal(1.0, edge.Weight); // 0.5 + 0.5 * (1 failed run / 1 run)
        Assert.Equal(900, edge.FeatureId);
    }

    [Fact]
    public async Task RecordExecution_Should_MarkSelectionsExecuted()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);
        Guid runId = Guid.NewGuid();

        await store.RecordRunAsync(Result(runId, "AREA", Mapped(101, 900, 0.9, 3)), CancellationToken.None);
        await store.RecordExecutionAsync(runId, [Outcome(101, "Failed", 12)], CancellationToken.None);

        await using var ctx = new Factory(connection).CreateDbContext();
        MappingSelection selection = await ctx.MappingSelections.SingleAsync(s => s.RunId == runId && s.TestCaseId == 101);
        Assert.True(selection.WasExecuted);
    }

    [Fact]
    public async Task RecordEscape_Should_PersistEscapeRecord()
    {
        using SqliteConnection connection = await OpenAsync();
        OutcomeStore store = Store(connection);

        await store.RecordEscapeAsync("AREA", 999, "manual", "missed a field bug", CancellationToken.None);

        await using var ctx = new Factory(connection).CreateDbContext();
        EscapeRecord escape = await ctx.EscapeRecords.SingleAsync();
        Assert.Equal(999, escape.TestCaseId);
        Assert.Equal("manual", escape.Source);
    }

    private sealed class Factory : IDbContextFactory<OutcomeDbContext>
    {
        private readonly DbContextOptions<OutcomeDbContext> _options;

        public Factory(SqliteConnection connection)
            => _options = new DbContextOptionsBuilder<OutcomeDbContext>().UseSqlite(connection).Options;

        public OutcomeDbContext CreateDbContext() => new(_options);

        public Task<OutcomeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
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
