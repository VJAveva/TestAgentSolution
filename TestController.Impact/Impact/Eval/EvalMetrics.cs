namespace TestControllerGrpc.Core.Impact.Eval;

/// <summary>One area's replay outcome: what the pipeline produced versus ground truth (P28).</summary>
public sealed record EvalCase(
    string AreaId,
    IReadOnlyList<int> ProducedSelection,
    IReadOnlyCollection<int> GroundTruthSelection,
    IReadOnlyCollection<int> FailedTests,
    double SelectedRuntimeSeconds,
    double FullSuiteRuntimeSeconds,
    bool EarlyExit);

/// <summary>Aggregate evaluation report for one configuration (P28).</summary>
public sealed record EvalReport(
    int Cases,
    double SafeRecall,
    double EarlyExitRate,
    double SafeRecallWithinEarlyExit,
    IReadOnlyDictionary<int, double> RecallAtK,
    IReadOnlyDictionary<int, double> PrecisionAtK,
    double MeanApfd,
    double MeanCost);

/// <summary>
/// Evaluation metrics for the impact-mapping harness (P28). Safe recall — the fraction of areas where every
/// test that actually failed was selected — is the metric that matters; a configuration with better precision
/// and worse safe recall is a worse configuration. All functions are pure so the harness is reproducible.
/// </summary>
public static class EvalMetrics
{
    /// <summary>Fraction of ground-truth selections present in the top <paramref name="k"/> of the produced order.</summary>
    public static double RecallAtK(IReadOnlyCollection<int> groundTruth, IReadOnlyList<int> produced, int k)
    {
        if (groundTruth.Count == 0)
        {
            return 1.0;
        }

        var topK = produced.Take(Math.Max(0, k)).ToHashSet();
        return (double)groundTruth.Count(topK.Contains) / groundTruth.Count;
    }

    /// <summary>Fraction of the top <paramref name="k"/> produced selections that are in the ground truth.</summary>
    public static double PrecisionAtK(IReadOnlyCollection<int> groundTruth, IReadOnlyList<int> produced, int k)
    {
        List<int> topK = produced.Take(Math.Max(0, k)).ToList();
        if (topK.Count == 0)
        {
            return 0.0;
        }

        var truth = groundTruth.ToHashSet();
        return (double)topK.Count(truth.Contains) / topK.Count;
    }

    /// <summary>True when every failed test was selected — the safety property the whole system optimises for.</summary>
    public static bool IsSafeRecall(IReadOnlyCollection<int> failedTests, IReadOnlyCollection<int> selected)
    {
        if (failedTests.Count == 0)
        {
            return true;
        }

        var selectedSet = selected.ToHashSet();
        return failedTests.All(selectedSet.Contains);
    }

    /// <summary>
    /// Average Percentage of Faults Detected for an ordered selection (P28). Measures ordering quality:
    /// <c>1 - (sum of first-detection positions) / (n * m) + 1 / (2n)</c>. Faults not detected by the selection
    /// are charged position n+1.
    /// </summary>
    public static double Apfd(IReadOnlyList<int> orderedTests, IReadOnlyCollection<int> faultRevealingTests)
    {
        int n = orderedTests.Count;
        if (n == 0 || faultRevealingTests.Count == 0)
        {
            return 0.0;
        }

        double positionSum = 0;
        foreach (int fault in faultRevealingTests)
        {
            int index = IndexOf(orderedTests, fault);
            positionSum += index >= 0 ? index + 1 : n + 1;
        }

        int m = faultRevealingTests.Count;
        return 1.0 - (positionSum / ((double)n * m)) + (1.0 / (2.0 * n));
    }

    /// <summary>Selected runtime as a fraction of the full-suite runtime (0 when the full suite has no runtime).</summary>
    public static double Cost(double selectedRuntimeSeconds, double fullSuiteRuntimeSeconds)
        => fullSuiteRuntimeSeconds <= 0 ? 0.0 : selectedRuntimeSeconds / fullSuiteRuntimeSeconds;

    /// <summary>Aggregates per-area cases into a configuration-level report.</summary>
    public static EvalReport Aggregate(IReadOnlyList<EvalCase> cases, IReadOnlyList<int> ks)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(ks);

        if (cases.Count == 0)
        {
            return new EvalReport(0, 1.0, 0.0, 1.0, new Dictionary<int, double>(), new Dictionary<int, double>(), 0, 0);
        }

        double safeRecall = cases.Average(c => IsSafeRecall(c.FailedTests, c.ProducedSelection) ? 1.0 : 0.0);
        double earlyExitRate = cases.Average(c => c.EarlyExit ? 1.0 : 0.0);

        List<EvalCase> earlyExitCases = cases.Where(c => c.EarlyExit).ToList();
        double safeRecallWithinEarlyExit = earlyExitCases.Count == 0
            ? 1.0
            : earlyExitCases.Average(c => IsSafeRecall(c.FailedTests, c.ProducedSelection) ? 1.0 : 0.0);

        var recallAtK = ks.ToDictionary(k => k, k => cases.Average(c => RecallAtK(c.GroundTruthSelection, c.ProducedSelection, k)));
        var precisionAtK = ks.ToDictionary(k => k, k => cases.Average(c => PrecisionAtK(c.GroundTruthSelection, c.ProducedSelection, k)));
        double meanApfd = cases.Average(c => Apfd(c.ProducedSelection, c.FailedTests));
        double meanCost = cases.Average(c => Cost(c.SelectedRuntimeSeconds, c.FullSuiteRuntimeSeconds));

        return new EvalReport(cases.Count, safeRecall, earlyExitRate, safeRecallWithinEarlyExit, recallAtK, precisionAtK, meanApfd, meanCost);
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
