namespace TestControllerGrpc.Core.Impact.Selection;

/// <summary>Detects regression areas and features left without adequate selected coverage (P23).</summary>
public interface ICoverageGapDetector
{
    /// <summary>Returns the coverage gaps for a selection — a first-class output, surfaced, never logged away.</summary>
    IReadOnlyList<CoverageGap> Detect(
        ImpactedArea area, IReadOnlyList<MappedTestCase> selected, IReadOnlyList<Scored<FeatureCandidate>> features);
}

/// <summary>
/// Surfaces coverage gaps (P23): a changed area with no mapped test is the single most actionable thing this
/// pipeline produces. Flags declared regression areas with no selection, selected features with no test in the
/// output (so a feature with zero coverage is a real gap rather than a truncation artefact), selections backed
/// by nothing but text similarity, and high-risk areas that came up short. Each gap carries the area risk tier
/// so the Regression tab can sort by it.
/// </summary>
public sealed class CoverageGapDetector : ICoverageGapDetector
{
    private const int HighRiskMinimumSelections = 3;

    /// <inheritdoc />
    public IReadOnlyList<CoverageGap> Detect(
        ImpactedArea area, IReadOnlyList<MappedTestCase> selected, IReadOnlyList<Scored<FeatureCandidate>> features)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(features);

        var gaps = new List<CoverageGap>();

        // A declared regression area with zero selected test cases (determinable when nothing was selected).
        if (selected.Count == 0)
        {
            foreach (string regressionArea in area.DeclaredRegressionAreas)
            {
                gaps.Add(new CoverageGap(regressionArea, "no test cases selected for declared regression area", area.RiskTier));
            }
        }

        // A selected feature with zero selected test cases in the output.
        var coveredFeatureIds = selected.Select(m => m.FeatureId).ToHashSet();
        foreach (Scored<FeatureCandidate> feature in features)
        {
            if (!coveredFeatureIds.Contains(feature.Value.Item.Id))
            {
                gaps.Add(new CoverageGap(feature.Value.Item.Title, "selected feature has no test cases in the output", area.RiskTier));
            }
        }

        // An area whose entire selection is backed by nothing but text similarity.
        if (selected.Count > 0 && selected.All(m => m.Confidence == MappingConfidence.Assumed))
        {
            gaps.Add(new CoverageGap(area.DisplayName, "no evidenced mapping", area.RiskTier));
        }

        // A high-risk area that came up short.
        if (area.RiskTier is RiskTier.Critical or RiskTier.High && selected.Count < HighRiskMinimumSelections)
        {
            gaps.Add(new CoverageGap(
                area.DisplayName,
                $"{area.RiskTier} risk area with only {selected.Count} selected test case(s)",
                area.RiskTier));
        }

        return gaps
            .GroupBy(g => (g.RegressionArea, g.Reason))
            .Select(g => g.First())
            .ToList();
    }
}
