using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Index;

namespace TestControllerGrpc.Core.Impact.Selection;

/// <summary>Selects the final test-case set under a time budget, balancing relevance, diversity and recall (P22).</summary>
public interface IBudgetedSelector
{
    /// <summary>Runs the six-step selection cascade and returns the mapped test cases plus diagnostics.</summary>
    IReadOnlyList<MappedTestCase> Select(
        ImpactedArea area,
        IReadOnlyList<Scored<FeatureCandidate>> features,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates,
        IReadOnlyDictionary<int, RelevanceJudgement> judgements,
        AnchorResult anchors,
        IReadOnlyDictionary<int, double> failureRates,
        IReadOnlyDictionary<int, TimeSpan> durations,
        IndexSnapshot snapshot,
        SelectionTier tier,
        TimeSpan budget,
        out SelectionDiagnostics diagnostics);
}

/// <summary>
/// Budgeted diversity selector (P22). Filters grade-0 noise, scores each candidate on a weighted blend of
/// calibrated retrieval, parent-feature strength, graded confidence, historical failure and an automation
/// tie-breaker, then applies MMR diversity over the index's dense vectors (diverse tests catch more bugs than
/// near-identical ones), a greedy value/cost budget knapsack, and — crucially — a recall safety net that is
/// never subject to the budget. The safety net (linked-work-item anchors, top historical failures,
/// high-confidence grade-3, and one test per selected feature) is what makes early exit and aggressive budgets
/// safe. Every selection carries a <see cref="MappingConfidence"/> and full provenance.
/// </summary>
public sealed class BudgetedDiversitySelector : IBudgetedSelector
{
    private const double RetrievalWeight = 0.45;
    private const double FeatureWeight = 0.25;
    private const double GradeWeight = 0.20;
    private const double FailureWeight = 0.10;
    private const double AutomationBonus = 0.05;
    private const double DefaultDurationSeconds = 60;

    private readonly ImpactMappingOptions.SelectionOptions _selection;
    private readonly ImpactMappingOptions.RerankOptions _rerank;

    /// <summary>Creates the selector from impact-mapping options.</summary>
    public BudgetedDiversitySelector(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _selection = options.Value.Selection;
        _rerank = options.Value.Rerank;
    }

    /// <inheritdoc />
    public IReadOnlyList<MappedTestCase> Select(
        ImpactedArea area,
        IReadOnlyList<Scored<FeatureCandidate>> features,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates,
        IReadOnlyDictionary<int, RelevanceJudgement> judgements,
        AnchorResult anchors,
        IReadOnlyDictionary<int, double> failureRates,
        IReadOnlyDictionary<int, TimeSpan> durations,
        IndexSnapshot snapshot,
        SelectionTier tier,
        TimeSpan budget,
        out SelectionDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(anchors);

        Dictionary<int, Scored<TestCaseCandidate>> byId = candidates
            .GroupBy(c => c.Value.Item.Id)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Score).First());

        Dictionary<int, double> featureScores = features
            .GroupBy(f => f.Value.Item.Id)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Score));
        double maxFeature = featureScores.Count > 0 ? featureScores.Values.Max() : 0;
        double maxRetrieval = byId.Count > 0 ? byId.Values.Max(c => c.Score) : 0;

        // Step 1-2: downgrade uncited judgements, score every candidate.
        List<Working> all = byId.Values
            .Select(sc => Score(sc, judgements, featureScores, failureRates, maxFeature, maxRetrieval))
            .ToList();

        List<Working> eligible = all.Where(w => w.Judgement.Grade > 0).ToList(); // Step 1: drop grade 0
        int droppedByGrade = all.Count - eligible.Count;

        // Step 3: MMR diversity.
        List<Working> diverse = MmrSelect(eligible, snapshot);
        int droppedByDiversity = eligible.Count - diverse.Count;

        // Step 4: greedy value/cost budget knapsack.
        double medianDuration = MedianSeconds(durations);
        double budgetSeconds = budget.TotalSeconds;
        var budgeted = new List<Working>();
        var droppedByBudget = new List<Working>();
        double usedSeconds = 0;
        foreach (Working w in diverse.OrderByDescending(w => Value(w) / Cost(w, durations, medianDuration)))
        {
            double cost = Cost(w, durations, medianDuration);
            if (usedSeconds + cost <= budgetSeconds)
            {
                budgeted.Add(w);
                usedSeconds += cost;
            }
            else
            {
                droppedByBudget.Add(w);
            }
        }

        // Step 5: recall safety net (bypasses the budget entirely).
        HashSet<int> mandatory = SafetyNet(all, anchors, features);
        var selected = new List<Working>(budgeted);
        var selectedIds = new HashSet<int>(budgeted.Select(w => w.TestCase.Item.Id));
        foreach (Working w in all)
        {
            if (mandatory.Contains(w.TestCase.Item.Id) && selectedIds.Add(w.TestCase.Item.Id))
            {
                selected.Add(w);
            }
        }

        // Step 6-7: confidence + provenance, ordered by final score.
        List<MappedTestCase> mapped = selected
            .OrderByDescending(w => w.FinalScore)
            .ThenBy(w => w.TestCase.Item.Id)
            .Select(w => ToMappedTestCase(w, area, anchors, features, featureScores))
            .ToList();

        Working? marginal = droppedByBudget
            .Where(w => !selectedIds.Contains(w.TestCase.Item.Id))
            .OrderByDescending(w => w.FinalScore)
            .FirstOrDefault();

        diagnostics = new SelectionDiagnostics(
            budget,
            TimeSpan.FromSeconds(selected.Sum(w => Cost(w, durations, medianDuration))),
            droppedByBudget.Count(w => !selectedIds.Contains(w.TestCase.Item.Id)),
            droppedByGrade,
            droppedByDiversity,
            marginal is null ? null : ToMappedTestCase(marginal, area, anchors, features, featureScores));

        return mapped;
    }

    private Working Score(
        Scored<TestCaseCandidate> scored, IReadOnlyDictionary<int, RelevanceJudgement> judgements,
        Dictionary<int, double> featureScores, IReadOnlyDictionary<int, double> failureRates,
        double maxFeature, double maxRetrieval)
    {
        TestCaseCandidate testCase = scored.Value;
        int id = testCase.Item.Id;
        int featureId = testCase.ParentFeatureId ?? -1;
        RelevanceJudgement judgement = JudgementFor(id, judgements);

        double retrievalNorm = maxRetrieval > 0 ? scored.Score / maxRetrieval : 0;
        double featureNorm = maxFeature > 0 && featureId >= 0 ? featureScores.GetValueOrDefault(featureId) / maxFeature : 0;
        double gradeTerm = judgement.Grade / 3.0 * judgement.Confidence;
        double failureRate = failureRates.GetValueOrDefault(id);
        bool automated = string.Equals(testCase.AutomationStatus, "Automated", StringComparison.OrdinalIgnoreCase);

        double finalScore =
            (RetrievalWeight * retrievalNorm)
            + (FeatureWeight * featureNorm)
            + (GradeWeight * gradeTerm)
            + (FailureWeight * failureRate)
            + (automated ? AutomationBonus : 0);

        return new Working(testCase, featureId, finalScore, judgement, failureRate);
    }

    private RelevanceJudgement JudgementFor(int id, IReadOnlyDictionary<int, RelevanceJudgement> judgements)
    {
        if (!judgements.TryGetValue(id, out RelevanceJudgement? judgement))
        {
            return new RelevanceJudgement(2, 0.5, "no judgement", []);
        }

        int downgrade = (int)Math.Round(_rerank.DowngradeWhenNoCitedSignals);
        return judgement.CitedSignals.Count == 0 && downgrade > 0
            ? judgement with { Grade = Math.Max(0, judgement.Grade - downgrade) }
            : judgement;
    }

    private List<Working> MmrSelect(List<Working> eligible, IndexSnapshot snapshot)
    {
        var selected = new List<Working>();
        var featureCounts = new Dictionary<int, int>();
        var pool = eligible.OrderByDescending(w => w.FinalScore).ToList();
        double lambda = _selection.MmrLambda;

        while (selected.Count < _selection.MaxTestCasesTotal && pool.Count > 0)
        {
            Working? best = null;
            double bestObjective = double.NegativeInfinity;
            foreach (Working candidate in pool)
            {
                if (featureCounts.GetValueOrDefault(candidate.FeatureId) >= _selection.MaxTestCasesPerFeature)
                {
                    continue;
                }

                double similarity = MaxSimilarity(candidate, selected, snapshot);
                double objective = (lambda * candidate.FinalScore) - ((1 - lambda) * similarity);
                if (objective > bestObjective)
                {
                    bestObjective = objective;
                    best = candidate;
                }
            }

            if (best is null)
            {
                break;
            }

            selected.Add(best);
            pool.Remove(best);
            featureCounts[best.FeatureId] = featureCounts.GetValueOrDefault(best.FeatureId) + 1;
        }

        return selected;
    }

    private static double MaxSimilarity(Working candidate, List<Working> selected, IndexSnapshot snapshot)
    {
        float[]? vector = snapshot.VectorFor(candidate.TestCase.Item.Id);
        if (vector is null || selected.Count == 0)
        {
            return 0;
        }

        double max = 0;
        foreach (Working other in selected)
        {
            float[]? otherVector = snapshot.VectorFor(other.TestCase.Item.Id);
            if (otherVector is not null && otherVector.Length == vector.Length)
            {
                max = Math.Max(max, CosineSimilarity(vector, otherVector));
            }
        }

        return max;
    }

    private HashSet<int> SafetyNet(List<Working> all, AnchorResult anchors, IReadOnlyList<Scored<FeatureCandidate>> features)
    {
        var mandatory = new HashSet<int>();

        foreach (AnchorEdge edge in anchors.Edges.Where(e => e.Source == AnchorSource.LinkedWorkItem))
        {
            mandatory.Add(edge.TestCaseId);
        }

        foreach (AnchorEdge edge in anchors.Edges
            .Where(e => e.Source == AnchorSource.HistoricalFailure)
            .OrderByDescending(e => e.Weight)
            .Take(3))
        {
            mandatory.Add(edge.TestCaseId);
        }

        foreach (Working w in all.Where(w => w.Judgement.Grade == 3 && w.Judgement.Confidence >= 0.8))
        {
            mandatory.Add(w.TestCase.Item.Id);
        }

        var selectedFeatureIds = features.Select(f => f.Value.Item.Id).ToHashSet();
        foreach (IGrouping<int, Working> group in all.Where(w => selectedFeatureIds.Contains(w.FeatureId)).GroupBy(w => w.FeatureId))
        {
            mandatory.Add(group.OrderByDescending(w => w.FinalScore).First().TestCase.Item.Id);
        }

        return mandatory;
    }

    private MappedTestCase ToMappedTestCase(
        Working w, ImpactedArea area, AnchorResult anchors,
        IReadOnlyList<Scored<FeatureCandidate>> features, Dictionary<int, double> featureScores)
    {
        int id = w.TestCase.Item.Id;
        AnchorSource? anchor = anchors.Edges
            .Where(e => e.TestCaseId == id)
            .Select(e => (AnchorSource?)e.Source)
            .FirstOrDefault();

        FeatureCandidate? feature = features.FirstOrDefault(f => f.Value.Item.Id == w.FeatureId)?.Value;
        MappingConfidence confidence = AssignConfidence(w, anchor, feature);

        var provenance = new List<ProvenanceLink>
        {
            new("area", area.DisplayName, null),
            new("retrieval", "hybrid retrieval", w.TestCase.Item.Id),
            new("feature", feature?.Item.Title ?? $"feature {w.FeatureId}", w.FeatureId >= 0 ? featureScores.GetValueOrDefault(w.FeatureId) : null),
            new("discoveryPath", feature?.DiscoveryPath.ToString() ?? "none", null),
            new("grade", $"grade {w.Judgement.Grade}: {w.Judgement.Reason}", w.Judgement.Grade),
            new("selection", confidence.ToString(), w.FinalScore),
        };

        return new MappedTestCase(w.TestCase, w.FeatureId, w.FinalScore, confidence, w.Judgement, anchor, provenance);
    }

    private static MappingConfidence AssignConfidence(Working w, AnchorSource? anchor, FeatureCandidate? feature)
    {
        if (anchor is not null)
        {
            return MappingConfidence.Observed;
        }

        if (feature is not null
            && feature.DiscoveryPath.HasFlag(FeatureDiscoveryPath.DirectFeatureSearch)
            && feature.DiscoveryPath.HasFlag(FeatureDiscoveryPath.TestCaseBackReference))
        {
            return MappingConfidence.Observed;
        }

        return w.TestCase.ParentFeatureId is not null ? MappingConfidence.Declared : MappingConfidence.Assumed;
    }

    private double Value(Working w) => w.FinalScore * Math.Max(w.FailureRate, 0.05);

    private static double Cost(Working w, IReadOnlyDictionary<int, TimeSpan> durations, double medianSeconds)
        => durations.TryGetValue(w.TestCase.Item.Id, out TimeSpan duration) ? duration.TotalSeconds : medianSeconds;

    private static double MedianSeconds(IReadOnlyDictionary<int, TimeSpan> durations)
    {
        if (durations.Count == 0)
        {
            return DefaultDurationSeconds;
        }

        double[] seconds = durations.Values.Select(d => d.TotalSeconds).OrderBy(s => s).ToArray();
        return seconds[seconds.Length / 2];
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private sealed record Working(TestCaseCandidate TestCase, int FeatureId, double FinalScore, RelevanceJudgement Judgement, double FailureRate);
}
