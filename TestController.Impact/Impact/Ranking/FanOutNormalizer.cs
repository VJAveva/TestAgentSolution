using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Index;

namespace TestControllerGrpc.Core.Impact.Ranking;

/// <summary>Damps hub features that link to far more test cases than the corpus median (P17).</summary>
public interface IFanOutNormalizer
{
    /// <summary>Applies a multiplicative fan-out penalty and records a warning for hub features.</summary>
    IReadOnlyList<Scored<FeatureCandidate>> Normalize(
        IReadOnlyList<Scored<FeatureCandidate>> features, IndexSnapshot snapshot, IList<string> warnings);
}

/// <summary>
/// Fan-out normalizer (P17). A hub feature such as "Common Framework" with hundreds of linked test cases
/// matches almost every query and floods candidate expansion with noise, while a feature with a handful of
/// test cases that matches is a far more specific signal. An IDF-style multiplicative penalty —
/// <c>log(1 + median) / log(1 + childCount)</c>, clamped to the configured floor/ceiling — mildly boosts
/// below-median features and damps hubs, but never zeroes a score: a hub with an extremely strong text match
/// may still deserve selection. Hubs beyond the warning multiple are surfaced so the UI can flag them.
/// </summary>
public sealed class FanOutNormalizer : IFanOutNormalizer
{
    private readonly ImpactMappingOptions.RetrievalOptions _options;

    /// <summary>Creates the normalizer from impact-mapping retrieval options.</summary>
    public FanOutNormalizer(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Retrieval;
    }

    /// <inheritdoc />
    public IReadOnlyList<Scored<FeatureCandidate>> Normalize(
        IReadOnlyList<Scored<FeatureCandidate>> features, IndexSnapshot snapshot, IList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(warnings);

        int median = snapshot.MedianChildCount;
        double numerator = Math.Log(1 + median);

        var result = new List<Scored<FeatureCandidate>>(features.Count);
        foreach (Scored<FeatureCandidate> scored in features)
        {
            FeatureCandidate feature = scored.Value;
            int childCount = feature.ChildTestCaseCount;
            double penalty = ComputePenalty(median, numerator, childCount);
            double newScore = scored.Score * penalty;

            var components = scored.Components.Append(new ScoreComponent("fanOut", childCount, penalty, newScore)).ToList();
            result.Add(new Scored<FeatureCandidate>(feature, newScore, components));

            if (median > 0 && childCount > median * _options.HubWarningMultiple)
            {
                warnings.Add(
                    $"Feature {feature.Item.Id} '{feature.Item.Title}' is low-specificity: {childCount} linked test cases (median {median}).");
            }
        }

        return result
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Value.Item.Id)
            .ToList();
    }

    private double ComputePenalty(int median, double numerator, int childCount)
    {
        if (median <= 0)
        {
            return 1.0; // no fan-out data → no adjustment
        }

        // A below-median (including zero-child) feature gets the ceiling boost; a hub gets damped toward the floor.
        double denominator = Math.Log(1 + childCount);
        double penalty = denominator <= 0 ? _options.FanOutPenaltyCeiling : numerator / denominator;
        return Math.Clamp(penalty, _options.FanOutPenaltyFloor, _options.FanOutPenaltyCeiling);
    }
}
