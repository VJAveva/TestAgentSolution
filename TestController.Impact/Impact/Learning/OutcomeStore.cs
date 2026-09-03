using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Learning;

/// <summary>Durable store of runs, selections, executions and escapes that feeds the learning loop (P20).</summary>
public interface IOutcomeStore
{
    /// <summary>Persists a completed run and its selections.</summary>
    Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct);

    /// <summary>Records the observed execution outcomes for a run and flags the selections as executed.</summary>
    Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct);

    /// <summary>Records a field escape — a test case that should have been selected for an area but was not.</summary>
    Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct);

    /// <summary>Returns historical-failure anchor edges for an area over the last <paramref name="lookbackRuns"/> runs.</summary>
    Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct);

    /// <summary>Returns the failure rate (failed / total executions) for each requested test case.</summary>
    Task<IReadOnlyDictionary<int, double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Returns the mean observed duration for each requested test case.</summary>
    Task<IReadOnlyDictionary<int, TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Returns (final score, outcome label) pairs for offline calibrator/ranker training.</summary>
    Task<IReadOnlyList<LabelledScore>> GetTrainingPairsAsync(CancellationToken ct);
}

/// <summary>
/// EF Core outcome store (P20): the component that turns a static heuristic into something that improves
/// every release. Selections, executions and — most valuably — field escapes accumulate here, permanently,
/// separate from the rebuildable index. Escapes and historical failures strengthen Tier-0 anchors for the
/// next change in an area; scores paired with outcomes feed offline calibration (P19/P28). Writes are simple
/// and idempotent per run; reads are aggregate queries used by the anchor provider and the selector.
/// </summary>
public sealed class OutcomeStore : IOutcomeStore
{
    private const string FailedResult = "Failed";
    private const string ModelVersion = "impact-1";

    private readonly IDbContextFactory<OutcomeDbContext> _contextFactory;
    private readonly ImpactMappingOptions.LearningOptions _options;
    private readonly IAppLogger _logger;

    /// <summary>Creates the store over the outcome database context factory.</summary>
    public OutcomeStore(
        IDbContextFactory<OutcomeDbContext> contextFactory, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _options = options.Value.Learning;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        ctx.MappingRuns.Add(new MappingRun
        {
            Id = result.RunId,
            AreaId = result.Area.AreaId,
            PullRequestId = null,
            Tier = result.Tier,
            CreatedUtc = DateTimeOffset.UtcNow,
            ModelVersion = ModelVersion,
            ScoringMode = _options.ScoringMode,
            SelectedCount = result.MappedTestCases.Count,
            BudgetUsedSeconds = result.Diagnostics.BudgetUsed.TotalSeconds,
            EarlyExit = result.EarlyExit,
        });

        int rank = 0;
        foreach (MappedTestCase mapped in result.MappedTestCases)
        {
            ctx.MappingSelections.Add(new MappingSelection
            {
                RunId = result.RunId,
                TestCaseId = mapped.TestCase.Item.Id,
                FeatureId = mapped.FeatureId,
                FinalScore = mapped.FinalScore,
                Grade = mapped.Judgement?.Grade ?? 2,
                AnchorSource = mapped.Anchor,
                Rank = rank++,
                WasExecuted = false,
            });
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
        {
            return;
        }

        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        foreach (ExecutionOutcome outcome in outcomes)
        {
            outcome.RunId = runId;
            ctx.ExecutionOutcomes.Add(outcome);
        }

        int[] executedIds = outcomes.Select(o => o.TestCaseId).Distinct().ToArray();
        List<MappingSelection> selections = await ctx.MappingSelections
            .Where(s => s.RunId == runId && executedIds.Contains(s.TestCaseId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (MappingSelection selection in selections)
        {
            selection.WasExecuted = true;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct)
    {
        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        ctx.EscapeRecords.Add(new EscapeRecord
        {
            Id = Guid.NewGuid(),
            AreaId = areaId,
            TestCaseId = testCaseId,
            DetectedUtc = DateTimeOffset.UtcNow,
            Source = source,
            Notes = notes,
        });

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Info("ImpactOutcome", $"Recorded escape for area {areaId}, test case {testCaseId} (source: {source}).");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct)
    {
        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        // SQLite cannot ORDER BY a DateTimeOffset, so take the most-recent runs client-side.
        List<Guid> runs = (await ctx.MappingRuns
                .Where(r => r.AreaId == areaId)
                .Select(r => new { r.Id, r.CreatedUtc })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .OrderByDescending(r => r.CreatedUtc)
            .Take(Math.Max(1, lookbackRuns))
            .Select(r => r.Id)
            .ToList();

        if (runs.Count == 0)
        {
            return [];
        }

        var failures = await ctx.ExecutionOutcomes
            .Where(o => runs.Contains(o.RunId) && o.Result == FailedResult)
            .Select(o => new { o.RunId, o.TestCaseId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var featureByTestCase = (await ctx.MappingSelections
                .Where(s => runs.Contains(s.RunId))
                .Select(s => new { s.TestCaseId, s.FeatureId })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(s => s.TestCaseId)
            .ToDictionary(g => g.Key, g => (int?)g.First().FeatureId);

        int runCount = runs.Count;
        return failures
            .GroupBy(f => f.TestCaseId)
            .Select(g =>
            {
                int failedRuns = g.Select(x => x.RunId).Distinct().Count();
                double weight = Math.Min(1.0, 0.5 + (0.5 * failedRuns / runCount));
                return new AnchorEdge(
                    g.Key,
                    featureByTestCase.GetValueOrDefault(g.Key),
                    AnchorSource.HistoricalFailure,
                    weight,
                    $"Failed in {failedRuns}/{runCount} recent runs for area {areaId}.");
            })
            .OrderByDescending(e => e.Weight)
            .ThenBy(e => e.TestCaseId)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, double>();
        }

        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        var rows = await ctx.ExecutionOutcomes
            .Where(o => ids.Contains(o.TestCaseId))
            .Select(o => new { o.TestCaseId, o.Result })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TestCaseId)
            .ToDictionary(g => g.Key, g => (double)g.Count(x => x.Result == FailedResult) / g.Count());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, TimeSpan>();
        }

        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        var rows = await ctx.ExecutionOutcomes
            .Where(o => ids.Contains(o.TestCaseId))
            .Select(o => new { o.TestCaseId, o.DurationSeconds })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TestCaseId)
            .ToDictionary(g => g.Key, g => TimeSpan.FromSeconds(g.Average(x => x.DurationSeconds)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LabelledScore>> GetTrainingPairsAsync(CancellationToken ct)
    {
        await using OutcomeDbContext ctx = await CreateAsync(ct).ConfigureAwait(false);

        var selections = await ctx.MappingSelections
            .Select(s => new { s.RunId, s.TestCaseId, s.FinalScore })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Dictionary<(Guid, int), string> outcomeByKey = (await ctx.ExecutionOutcomes
                .Select(o => new { o.RunId, o.TestCaseId, o.Result })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(o => (o.RunId, o.TestCaseId))
            .ToDictionary(g => g.Key, g => g.First().Result);

        var pairs = new List<LabelledScore>();
        foreach (var selection in selections)
        {
            if (outcomeByKey.TryGetValue((selection.RunId, selection.TestCaseId), out string? result))
            {
                pairs.Add(new LabelledScore(selection.FinalScore, result == FailedResult ? 1 : 0));
            }
        }

        return pairs;
    }

    private async Task<OutcomeDbContext> CreateAsync(CancellationToken ct)
    {
        OutcomeDbContext ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ctx.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        return ctx;
    }
}
