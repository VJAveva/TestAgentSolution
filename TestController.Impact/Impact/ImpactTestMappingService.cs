using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Features;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Core.Impact.Query;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Core.Impact.Rerank;
using TestControllerGrpc.Core.Impact.Retrieval;
using TestControllerGrpc.Core.Impact.Selection;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact;

/// <summary>The impact-mapping engine: resolves impacted Features and Test Cases for a change (P24).</summary>
public interface IImpactTestMappingService
{
    /// <summary>Runs the full cascade and returns the auditable result.</summary>
    Task<ImpactMappingResult> MapAsync(ImpactedArea area, ChangePayload payload, SelectionTier tier, CancellationToken ct);

    /// <summary>Runs the cascade, yielding one progress tick per completed tier; only the terminal tick carries a Result.</summary>
    IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(
        ImpactedArea area, ChangePayload payload, SelectionTier tier, CancellationToken ct);
}

/// <summary>
/// The cascade orchestrator (P24). Composes the whole engine — anchors, query construction, dual-branch
/// retrieval, rerank, calibration, budgeted selection and gap detection — behind one host-agnostic service.
/// Strong anchors short-circuit to selection (early exit) so per-PR runs stay affordable. Resilience is
/// central: a failing keyword group or optional dependency degrades to a warning, not a failed run; only a
/// total retrieval failure with no anchors is a hard error. No host type ever crosses into this assembly.
/// </summary>
public sealed class ImpactTestMappingService : IImpactTestMappingService
{
    private const int GroupConcurrency = 4;

    private readonly IAnchorEdgeProvider _anchors;
    private readonly IChangeDocumentBuilder _changeDocumentBuilder;
    private readonly IHydeQueryGenerator _hyde;
    private readonly IKeywordExtractor _keywords;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IRetrievalIndexStore _indexStore;
    private readonly IHybridRetriever _retriever;
    private readonly IFeatureRanker _featureRanker;
    private readonly IParentFeatureResolver _parentResolver;
    private readonly IFeatureMerger _merger;
    private readonly IFanOutNormalizer _fanOut;
    private readonly IAdoWorkItemClient _ado;
    private readonly IRelevanceReranker _reranker;
    private readonly IScoreCalibrator _calibrator;
    private readonly IBudgetedSelector _selector;
    private readonly ICoverageGapDetector _gapDetector;
    private readonly IOutcomeStore _outcomes;
    private readonly ImpactMappingOptions.RetrievalOptions _retrieval;
    private readonly ImpactMappingOptions.RerankOptions _rerank;
    private readonly ImpactMappingOptions.SelectionOptions _selection;
    private readonly ImpactMappingOptions.LearningOptions _learning;
    private readonly IAppLogger _logger;

    /// <summary>Creates the orchestrator from the full component set (the composition root).</summary>
    public ImpactTestMappingService(
        IAnchorEdgeProvider anchors, IChangeDocumentBuilder changeDocumentBuilder, IHydeQueryGenerator hyde,
        IKeywordExtractor keywords, IEmbeddingProvider embeddings, IRetrievalIndexStore indexStore,
        IHybridRetriever retriever, IFeatureRanker featureRanker, IParentFeatureResolver parentResolver,
        IFeatureMerger merger, IFanOutNormalizer fanOut, IAdoWorkItemClient ado, IRelevanceReranker reranker,
        IScoreCalibrator calibrator, IBudgetedSelector selector, ICoverageGapDetector gapDetector,
        IOutcomeStore outcomes, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _anchors = anchors;
        _changeDocumentBuilder = changeDocumentBuilder;
        _hyde = hyde;
        _keywords = keywords;
        _embeddings = embeddings;
        _indexStore = indexStore;
        _retriever = retriever;
        _featureRanker = featureRanker;
        _parentResolver = parentResolver;
        _merger = merger;
        _fanOut = fanOut;
        _ado = ado;
        _reranker = reranker;
        _calibrator = calibrator;
        _selector = selector;
        _gapDetector = gapDetector;
        _outcomes = outcomes;
        _retrieval = options.Value.Retrieval;
        _rerank = options.Value.Rerank;
        _selection = options.Value.Selection;
        _learning = options.Value.Learning;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ImpactMappingResult> MapAsync(ImpactedArea area, ChangePayload payload, SelectionTier tier, CancellationToken ct)
        => MapCoreAsync(area, payload, tier, progress: null, ct);

    /// <inheritdoc />
    public async IAsyncEnumerable<ImpactMappingProgress> MapWithProgressAsync(
        ImpactedArea area, ChangePayload payload, SelectionTier tier, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ImpactMappingProgress>();
        var progress = new ChannelProgress(channel.Writer);

        Task runner = Task.Run(async () =>
        {
            try
            {
                await MapCoreAsync(area, payload, tier, progress, ct).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        await foreach (ImpactMappingProgress tick in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return tick;
        }

        await runner.ConfigureAwait(false);
    }

    private async Task<ImpactMappingResult> MapCoreAsync(
        ImpactedArea area, ChangePayload payload, SelectionTier tier, IProgress<ImpactMappingProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(payload);

        var stopwatch = Stopwatch.StartNew();
        var runId = Guid.NewGuid();
        var warnings = new List<string>();

        // T0 — anchors and possible early exit.
        Report(progress, "T0:Anchors", 0, 6, "Resolving anchors…");
        AnchorResult anchors = await SafeAnchorsAsync(area, payload, warnings, ct).ConfigureAwait(false);
        if (anchors.SufficientForEarlyExit && tier != SelectionTier.Full)
        {
            ImpactMappingResult early = await EarlyExitAsync(area, anchors, tier, runId, warnings, stopwatch, ct).ConfigureAwait(false);
            Report(progress, "Done", 6, 6, "Complete (early exit on anchors).", early);
            return early;
        }

        // T1 — query construction.
        Report(progress, "T1:Query", 1, 6, "Building change document…");
        ChangeDocument changeDoc = _changeDocumentBuilder.Build(area, payload);
        HydeQuery hyde = await SafeHydeAsync(area, changeDoc, warnings, ct).ConfigureAwait(false);
        IReadOnlyList<KeywordGroup> groups = _keywords.Extract(area, changeDoc, hyde);
        float[]? denseQuery = await EmbedQueryAsync(hyde, warnings, ct).ConfigureAwait(false);

        // T2 — dual-branch retrieval and feature resolution.
        Report(progress, "T2:Retrieval", 2, 6, "Retrieving features and test cases…");
        Task<IndexSnapshot> featureSnapshotTask = _indexStore.GetSnapshotAsync(IndexKind.Feature, ct);
        Task<IndexSnapshot> testCaseSnapshotTask = _indexStore.GetSnapshotAsync(IndexKind.TestCase, ct);
        await Task.WhenAll(featureSnapshotTask, testCaseSnapshotTask).ConfigureAwait(false);
        IndexSnapshot featureSnapshot = featureSnapshotTask.Result;
        IndexSnapshot testCaseSnapshot = testCaseSnapshotTask.Result;

        Task<IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>>> branchATask =
            RunFeatureBranchAsync(groups, featureSnapshot, denseQuery, area, warnings, ct);
        Task<BranchBResult> branchBTask =
            RunTestCaseBranchAsync(groups, testCaseSnapshot, featureSnapshot, denseQuery, area, warnings, ct);
        await Task.WhenAll(branchATask, branchBTask).ConfigureAwait(false);
        IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>> branchA = branchATask.Result;
        BranchBResult branchB = branchBTask.Result;

        if (branchA.All(l => l.Count == 0) && branchB.PerGroup.All(l => l.Count == 0) && anchors.Edges.Count == 0)
        {
            throw new InvalidOperationException(
                "Impact mapping failed: both retrieval branches returned nothing and no anchors are available.");
        }

        IReadOnlyList<Scored<FeatureCandidate>> globalA = _featureRanker.RankGlobally(branchA, _retrieval.MaxFeaturesAfterMerge);
        IReadOnlyList<Scored<FeatureCandidate>> globalB = _featureRanker.RankGlobally(branchB.PerGroup, _retrieval.MaxFeaturesAfterMerge);
        IReadOnlyList<Scored<FeatureCandidate>> merged = _merger.Merge(globalA, globalB, _retrieval.MaxFeaturesAfterMerge);
        IReadOnlyList<Scored<FeatureCandidate>> normalized = _fanOut.Normalize(merged, featureSnapshot, warnings);

        IReadOnlyList<Scored<TestCaseCandidate>> candidates =
            await ExpandAndReduceCandidatesAsync(normalized, branchB.TestCases, branchB.Orphans, warnings, ct).ConfigureAwait(false);

        // Fold anchor test cases (linked work items, historical failures, declared map) into the candidate set so
        // Tier-0 evidence is still selectable when retrieval is thin/empty — otherwise anchors only help on early exit.
        (candidates, IReadOnlyDictionary<int, RelevanceJudgement> anchorJudgements) =
            await MergeAnchorCandidatesAsync(candidates, anchors, warnings, ct).ConfigureAwait(false);

        // T3 — rerank and calibrate.
        Report(progress, "T3:Rerank", 3, 6, "Reranking candidates…");
        IReadOnlyDictionary<int, RelevanceJudgement> judgements =
            await SafeRerankAsync(changeDoc, hyde, payload, candidates, warnings, ct).ConfigureAwait(false);
        if (anchorJudgements.Count > 0)
        {
            // Deterministic anchor evidence keeps its high grade even if the reranker degraded to pass-through.
            var overridden = new Dictionary<int, RelevanceJudgement>(judgements);
            foreach (KeyValuePair<int, RelevanceJudgement> kv in anchorJudgements)
                overridden[kv.Key] = kv.Value;
            judgements = overridden;
        }
        List<Scored<TestCaseCandidate>> calibrated = candidates
            .Select(c => c with { Score = _calibrator.Calibrate(c.Score, []) })
            .ToList();

        // T4 — outcomes, selection and gaps.
        Report(progress, "T4:Select", 4, 6, "Selecting under budget…");
        List<int> ids = candidates.Select(c => c.Value.Item.Id).Distinct().ToList();
        (IReadOnlyDictionary<int, double> failureRates, IReadOnlyDictionary<int, TimeSpan> durations) =
            await GetOutcomesAsync(ids, warnings, ct).ConfigureAwait(false);

        IReadOnlyList<MappedTestCase> selected = _selector.Select(
            area, normalized, calibrated, judgements, anchors, failureRates, durations,
            testCaseSnapshot, tier, BudgetFor(tier), out SelectionDiagnostics diagnostics);
        IReadOnlyList<CoverageGap> gaps = _gapDetector.Detect(area, selected, normalized);

        var result = new ImpactMappingResult(
            area, groups, normalized, selected, gaps, anchors, diagnostics, tier, EarlyExit: false, warnings, stopwatch.Elapsed, runId);

        // T5 — record for the learning loop.
        Report(progress, "T5:Record", 5, 6, "Recording outcome…");
        await RecordAsync(result, warnings, ct).ConfigureAwait(false);

        _logger.Info("ImpactMap",
            $"area={area.AreaId} tier={tier} selected={selected.Count} gaps={gaps.Count} warnings={warnings.Count} elapsed={stopwatch.Elapsed}");
        Report(progress, "Done", 6, 6, "Complete.", result);
        return result;
    }

    private async Task<ImpactMappingResult> EarlyExitAsync(
        ImpactedArea area, AnchorResult anchors, SelectionTier tier, Guid runId, List<string> warnings,
        Stopwatch stopwatch, CancellationToken ct)
    {
        int[] anchorIds = anchors.Edges.Select(e => e.TestCaseId).Distinct().ToArray();
        IReadOnlyList<TestCaseCandidate> testCases = anchorIds.Length > 0
            ? await SafeGetTestCasesAsync(anchorIds, warnings, ct).ConfigureAwait(false)
            : [];

        Dictionary<int, double> weightById = anchors.Edges
            .GroupBy(e => e.TestCaseId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Weight));

        List<Scored<TestCaseCandidate>> scored = testCases
            .Select(tc => new Scored<TestCaseCandidate>(tc, weightById.GetValueOrDefault(tc.Item.Id, 1.0), []))
            .ToList();
        Dictionary<int, RelevanceJudgement> judgements = scored
            .ToDictionary(s => s.Value.Item.Id, _ => new RelevanceJudgement(3, 0.9, "anchor edge", ["anchor"]));

        List<int> ids = scored.Select(s => s.Value.Item.Id).ToList();
        (IReadOnlyDictionary<int, double> failureRates, IReadOnlyDictionary<int, TimeSpan> durations) =
            await GetOutcomesAsync(ids, warnings, ct).ConfigureAwait(false);

        IReadOnlyList<MappedTestCase> selected = _selector.Select(
            area, [], scored, judgements, anchors, failureRates, durations,
            IndexSnapshot.Empty, tier, BudgetFor(tier), out SelectionDiagnostics diagnostics);
        IReadOnlyList<CoverageGap> gaps = _gapDetector.Detect(area, selected, []);

        var result = new ImpactMappingResult(
            area, [], [], selected, gaps, anchors, diagnostics, tier, EarlyExit: true, warnings, stopwatch.Elapsed, runId);
        await RecordAsync(result, warnings, ct).ConfigureAwait(false);
        return result;
    }

    private static readonly IReadOnlyDictionary<int, RelevanceJudgement> EmptyJudgements =
        new Dictionary<int, RelevanceJudgement>();

    // Adds anchor test cases (Tier-0) not already surfaced by retrieval into the candidate set, so linked-work-item
    // / historical evidence is selectable even when the index is thin or empty. Mirrors EarlyExitAsync's hydration.
    private async Task<(IReadOnlyList<Scored<TestCaseCandidate>> Candidates, IReadOnlyDictionary<int, RelevanceJudgement> AnchorJudgements)>
        MergeAnchorCandidatesAsync(IReadOnlyList<Scored<TestCaseCandidate>> candidates, AnchorResult anchors, List<string> warnings, CancellationToken ct)
    {
        if (anchors.Edges.Count == 0)
            return (candidates, EmptyJudgements);

        var have = candidates.Select(c => c.Value.Item.Id).ToHashSet();
        int[] missing = anchors.Edges.Select(e => e.TestCaseId).Distinct().Where(id => !have.Contains(id)).ToArray();
        if (missing.Length == 0)
            return (candidates, EmptyJudgements);

        IReadOnlyList<TestCaseCandidate> fetched = await SafeGetTestCasesAsync(missing, warnings, ct).ConfigureAwait(false);
        if (fetched.Count == 0)
            return (candidates, EmptyJudgements);

        Dictionary<int, double> weightById = anchors.Edges
            .GroupBy(e => e.TestCaseId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Weight));

        var merged = new List<Scored<TestCaseCandidate>>(candidates);
        var anchorJudgements = new Dictionary<int, RelevanceJudgement>();
        foreach (TestCaseCandidate tc in fetched)
        {
            double weight = weightById.GetValueOrDefault(tc.Item.Id, 1.0);
            merged.Add(new Scored<TestCaseCandidate>(tc, weight, [new ScoreComponent("anchor", weight, 1.0, weight)]));
            anchorJudgements[tc.Item.Id] = new RelevanceJudgement(3, 0.9, "anchor edge", ["anchor"]);
        }
        return (merged, anchorJudgements);
    }

    private async Task<IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>>> RunFeatureBranchAsync(
        IReadOnlyList<KeywordGroup> groups, IndexSnapshot featureSnapshot, float[]? denseQuery,
        ImpactedArea area, List<string> warnings, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(GroupConcurrency);
        IEnumerable<Task<IReadOnlyList<Scored<FeatureCandidate>>>> tasks = groups.Select(async group =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<Scored<int>> retrieved =
                    await _retriever.RetrieveAsync(featureSnapshot, group, denseQuery, _retrieval.TopFeaturesPerGroup, ct).ConfigureAwait(false);
                int[] featureIds = retrieved.Select(r => r.Value).ToArray();
                if (featureIds.Length == 0)
                {
                    return (IReadOnlyList<Scored<FeatureCandidate>>)[];
                }

                IReadOnlyList<FeatureCandidate> features = await _ado.GetFeaturesAsync(featureIds, ct).ConfigureAwait(false);
                List<FeatureCandidate> tagged = features
                    .Select(f => f with
                    {
                        DiscoveryPath = f.DiscoveryPath | FeatureDiscoveryPath.DirectFeatureSearch,
                        MatchedGroupIds = new[] { group.GroupId },
                    })
                    .ToList();
                return _featureRanker.RankWithinGroup(group, tagged, [], area, featureSnapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddWarning(warnings, $"Feature branch group {group.GroupId} degraded: {ex.Message}");
                return (IReadOnlyList<Scored<FeatureCandidate>>)[];
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private async Task<BranchBResult> RunTestCaseBranchAsync(
        IReadOnlyList<KeywordGroup> groups, IndexSnapshot testCaseSnapshot, IndexSnapshot featureSnapshot,
        float[]? denseQuery, ImpactedArea area, List<string> warnings, CancellationToken ct)
    {
        var testCasesById = new ConcurrentDictionary<int, Scored<TestCaseCandidate>>();
        var evidence = new ConcurrentBag<TestCaseEvidence>();
        var orphans = new ConcurrentBag<TestCaseCandidate>();

        using var gate = new SemaphoreSlim(GroupConcurrency);
        IEnumerable<Task<IReadOnlyList<Scored<FeatureCandidate>>>> tasks = groups.Select(async group =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<Scored<int>> retrieved =
                    await _retriever.RetrieveAsync(testCaseSnapshot, group, denseQuery, _retrieval.TopTestCasesPerGroup, ct).ConfigureAwait(false);
                if (retrieved.Count == 0)
                {
                    return (IReadOnlyList<Scored<FeatureCandidate>>)[];
                }

                Dictionary<int, double> scoreById = retrieved.ToDictionary(r => r.Value, r => r.Score);
                IReadOnlyList<TestCaseCandidate> hydrated = await _ado.GetTestCasesAsync(scoreById.Keys.ToArray(), ct).ConfigureAwait(false);
                List<Scored<TestCaseCandidate>> scoredTestCases = hydrated
                    .Select(tc => new Scored<TestCaseCandidate>(tc, scoreById.GetValueOrDefault(tc.Item.Id), []))
                    .ToList();
                foreach (Scored<TestCaseCandidate> scored in scoredTestCases)
                {
                    testCasesById[scored.Value.Item.Id] = scored;
                }

                ParentResolution resolution = await _parentResolver.ResolveAsync(group, scoredTestCases, ct).ConfigureAwait(false);
                foreach (TestCaseEvidence e in resolution.Evidence)
                {
                    evidence.Add(e);
                }

                foreach (TestCaseCandidate orphan in resolution.Orphans)
                {
                    orphans.Add(orphan);
                }

                return _featureRanker.RankWithinGroup(group, resolution.Features, resolution.Evidence, area, featureSnapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddWarning(warnings, $"Test-case branch group {group.GroupId} degraded: {ex.Message}");
                return (IReadOnlyList<Scored<FeatureCandidate>>)[];
            }
            finally
            {
                gate.Release();
            }
        });

        var perGroup = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
        return new BranchBResult(perGroup, testCasesById, evidence.ToList(), orphans.ToList());
    }

    private async Task<IReadOnlyList<Scored<TestCaseCandidate>>> ExpandAndReduceCandidatesAsync(
        IReadOnlyList<Scored<FeatureCandidate>> features, IReadOnlyDictionary<int, Scored<TestCaseCandidate>> retrieved,
        IReadOnlyList<TestCaseCandidate> orphans, List<string> warnings, CancellationToken ct)
    {
        var byId = new Dictionary<int, Scored<TestCaseCandidate>>(retrieved);

        int[] featureIds = features.Select(f => f.Value.Item.Id).Where(id => id >= 0).Distinct().ToArray();
        if (featureIds.Length > 0)
        {
            try
            {
                IReadOnlyDictionary<int, IReadOnlyList<int>> childMap = await _ado.GetChildTestCasesAsync(featureIds, ct).ConfigureAwait(false);
                int[] childIds = childMap.Values.SelectMany(x => x).Distinct().Where(id => !byId.ContainsKey(id)).ToArray();
                if (childIds.Length > 0)
                {
                    IReadOnlyList<TestCaseCandidate> childTestCases = await _ado.GetTestCasesAsync(childIds, ct).ConfigureAwait(false);
                    foreach (TestCaseCandidate tc in childTestCases)
                    {
                        byId.TryAdd(tc.Item.Id, new Scored<TestCaseCandidate>(tc, 0.1, []));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddWarning(warnings, $"Child test-case expansion degraded: {ex.Message}");
            }
        }

        foreach (TestCaseCandidate orphan in orphans)
        {
            byId.TryAdd(orphan.Item.Id, new Scored<TestCaseCandidate>(orphan, 0.05, []));
        }

        return byId.Values
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Value.Item.Id)
            .Take(Math.Max(1, _retrieval.MaxCandidateTestCases))
            .ToList();
    }

    private async Task<AnchorResult> SafeAnchorsAsync(ImpactedArea area, ChangePayload payload, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await _anchors.GetAnchorsAsync(area, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Anchor resolution degraded: {ex.Message}");
            return new AnchorResult([], 0, false);
        }
    }

    private async Task<HydeQuery> SafeHydeAsync(ImpactedArea area, ChangeDocument changeDoc, List<string> warnings, CancellationToken ct)
    {
        if (!_rerank.EnableHyde)
        {
            return FallbackHyde(area, changeDoc);
        }

        try
        {
            return await _hyde.GenerateAsync(changeDoc, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"HyDE generation degraded: {ex.Message}");
            return FallbackHyde(area, changeDoc);
        }
    }

    private async Task<IReadOnlyDictionary<int, RelevanceJudgement>> SafeRerankAsync(
        ChangeDocument changeDoc, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await _reranker.RerankAsync(changeDoc, hyde, payload, candidates, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Rerank degraded: {ex.Message}");
            return candidates.ToDictionary(c => c.Value.Item.Id, _ => new RelevanceJudgement(2, 0.5, "rerank unavailable", []));
        }
    }

    private async Task<IReadOnlyList<TestCaseCandidate>> SafeGetTestCasesAsync(int[] ids, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await _ado.GetTestCasesAsync(ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Anchor test-case hydration degraded: {ex.Message}");
            return [];
        }
    }

    private async Task<float[]?> EmbedQueryAsync(HydeQuery hyde, List<string> warnings, CancellationToken ct)
    {
        if (!_embeddings.IsEnabled)
        {
            return null;
        }

        try
        {
            string text = string.Join(' ', hyde.SyntheticTestTitles.Append(hyde.SyntheticTestBody).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            IReadOnlyList<float[]> vectors = await _embeddings.EmbedAsync([text], ct).ConfigureAwait(false);
            return vectors.Count > 0 && vectors[0].Length > 0 ? vectors[0] : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Query embedding degraded: {ex.Message}");
            return null;
        }
    }

    private async Task<(IReadOnlyDictionary<int, double> FailureRates, IReadOnlyDictionary<int, TimeSpan> Durations)> GetOutcomesAsync(
        IReadOnlyCollection<int> ids, List<string> warnings, CancellationToken ct)
    {
        try
        {
            Task<IReadOnlyDictionary<int, double>> failuresTask = _outcomes.GetFailureRatesAsync(ids, ct);
            Task<IReadOnlyDictionary<int, TimeSpan>> durationsTask = _outcomes.GetDurationsAsync(ids, ct);
            await Task.WhenAll(failuresTask, durationsTask).ConfigureAwait(false);
            return (failuresTask.Result, durationsTask.Result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Outcome lookup degraded: {ex.Message}");
            return (new Dictionary<int, double>(), new Dictionary<int, TimeSpan>());
        }
    }

    private async Task RecordAsync(ImpactMappingResult result, List<string> warnings, CancellationToken ct)
    {
        if (!_learning.RecordOutcomes)
        {
            return;
        }

        try
        {
            await _outcomes.RecordRunAsync(result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(warnings, $"Outcome recording degraded: {ex.Message}");
        }
    }

    private static HydeQuery FallbackHyde(ImpactedArea area, ChangeDocument changeDoc)
    {
        IReadOnlyList<string> titles = changeDoc.ChangedLiterals.Count > 0
            ? changeDoc.ChangedLiterals.Take(8).ToList()
            : changeDoc.ChangedSymbols.Take(8).ToList();
        string summary = !string.IsNullOrWhiteSpace(changeDoc.PrNarrative)
            ? changeDoc.PrNarrative!
            : $"Change affecting {area.DisplayName}.";
        return new HydeQuery(summary, titles, string.Join(' ', changeDoc.ChangedLiterals), []);
    }

    private TimeSpan BudgetFor(SelectionTier tier)
        => _selection.TierBudgets.TryGetValue(tier, out TimeSpan budget) ? budget : TimeSpan.MaxValue;

    private static void Report(
        IProgress<ImpactMappingProgress>? progress, string stage, int index, int total, string message, ImpactMappingResult? result = null)
        => progress?.Report(new ImpactMappingProgress(stage, index, total, message, result));

    private static void AddWarning(List<string> warnings, string message)
    {
        lock (warnings)
        {
            warnings.Add(message);
        }
    }

    private sealed record BranchBResult(
        IReadOnlyList<IReadOnlyList<Scored<FeatureCandidate>>> PerGroup,
        IReadOnlyDictionary<int, Scored<TestCaseCandidate>> TestCases,
        IReadOnlyList<TestCaseEvidence> Evidence,
        IReadOnlyList<TestCaseCandidate> Orphans);

    private sealed class ChannelProgress(ChannelWriter<ImpactMappingProgress> writer) : IProgress<ImpactMappingProgress>
    {
        public void Report(ImpactMappingProgress value) => writer.TryWrite(value);
    }
}
