namespace TestControllerGrpc.Core.Impact.Learning;

/// <summary>The upstream signals for one (change, test case) pair, ready to be flattened for calibration (P19).</summary>
public sealed record CalibrationFeatures(
    double Bm25Score,
    double DenseScore,
    double RrfScore,
    double FeatureScore,
    double FanOutPenalty,
    int LlmGrade,
    double LlmConfidence,
    int CitedSignalCount,
    double AnchorWeight,
    AnchorSource? AnchorSource,
    double HistoricalFailureRate,
    double DaysSinceLastRun,
    bool AutomationStatusFlag,
    bool SameSubsystemFlag,
    int AreaPathOverlapDepth,
    double TestCaseAgeDays,
    int ParentChildCount);

/// <summary>Flattens <see cref="CalibrationFeatures"/> into the fixed-order float vector calibrators consume (P19).</summary>
public interface IFeatureVectorExtractor
{
    /// <summary>Produces the fixed-length feature vector for one (change, test case) pair.</summary>
    float[] Extract(CalibrationFeatures features);
}

/// <summary>
/// Turns the signals already computed upstream into a fixed-order float vector (P19). The order and length
/// are stable and versioned via <see cref="FeatureNames"/> so a persisted ranker model and the live extractor
/// never disagree on column meaning. Pure and deterministic.
/// </summary>
public sealed class FeatureVectorExtractor : IFeatureVectorExtractor
{
    /// <summary>Number of features emitted (the vector length). Referenced by the ranker model schema.</summary>
    public const int FeatureCount = 20;

    /// <summary>Human-readable feature names, in emission order.</summary>
    public static IReadOnlyList<string> FeatureNames { get; } =
    [
        "bm25Score", "denseScore", "rrfScore", "featureScore", "fanOutPenalty",
        "llmGrade", "llmConfidence", "citedSignalCount",
        "anchorWeight", "anchorLinkedWorkItem", "anchorHistoricalFailure", "anchorDeclaredMapping", "anchorPriorSelection",
        "historicalFailureRate", "daysSinceLastRun", "automationStatusFlag",
        "sameSubsystemFlag", "areaPathOverlapDepth", "testCaseAgeDays", "parentChildCount",
    ];

    /// <inheritdoc />
    public float[] Extract(CalibrationFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var anchorOneHot = new float[4];
        if (features.AnchorSource is AnchorSource source)
        {
            anchorOneHot[(int)source] = 1f;
        }

        return
        [
            (float)features.Bm25Score,
            (float)features.DenseScore,
            (float)features.RrfScore,
            (float)features.FeatureScore,
            (float)features.FanOutPenalty,
            features.LlmGrade,
            (float)features.LlmConfidence,
            features.CitedSignalCount,
            (float)features.AnchorWeight,
            anchorOneHot[0],
            anchorOneHot[1],
            anchorOneHot[2],
            anchorOneHot[3],
            (float)features.HistoricalFailureRate,
            (float)features.DaysSinceLastRun,
            features.AutomationStatusFlag ? 1f : 0f,
            features.SameSubsystemFlag ? 1f : 0f,
            features.AreaPathOverlapDepth,
            (float)features.TestCaseAgeDays,
            features.ParentChildCount,
        ];
    }
}
