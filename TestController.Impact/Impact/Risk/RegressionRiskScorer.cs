using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.Impact.Risk;

/// <summary>Scores an impacted component's regression risk from its churn, defect and build signals (R1).</summary>
public interface IRegressionRiskScorer
{
    /// <summary>Returns the risk score, banded tier and populated churn metrics for one grid row.</summary>
    RegressionRiskAssessment Score(SubsystemRow row, DateTimeOffset nowUtc);
}

/// <summary>A component's computed regression risk, with the arithmetic preserved for provenance.</summary>
public sealed record RegressionRiskAssessment(
    double Score, RiskTier Tier, ChurnMetrics Churn, IReadOnlyList<ScoreComponent> Components);

/// <summary>
/// Default risk scorer (R1, docs/impact/Regression-Selection-Algorithm.md §2). Combines log-compressed churn,
/// saturating defect density, build-failure state, exponentially decaying recency and coverage uncertainty into
/// a convex score in [0,1], then bands it into <see cref="RiskTier"/>.
/// <para>
/// Never emits <see cref="RiskTier.Unmapped"/>: the enum is ordered so Unmapped sorts below Medium, and both
/// consumers (early-exit veto, coverage-gap minimum) treat it as the weakest tier. Emitting it for a
/// poorly-understood area would give the least-understood components the least protection. Uncertainty is
/// routed through the additive U term instead, which pushes such areas up the bands. Floor is Medium.
/// </para>
/// </summary>
public sealed class RegressionRiskScorer : IRegressionRiskScorer
{
    private const string SucceededResult = "succeeded";

    private readonly ImpactMappingOptions.RiskOptions _risk;

    /// <summary>Creates the scorer from impact-mapping options.</summary>
    public RegressionRiskScorer(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _risk = options.Value.Risk;
    }

    /// <inheritdoc />
    public RegressionRiskAssessment Score(SubsystemRow row, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);

        DateTimeOffset lastChanged = row.Changes.Count > 0
            ? row.Changes.Max(c => c.ObservedUtc)
            : DateTimeOffset.UnixEpoch;

        double churn = ChurnMagnitude(row.TotalFilesModified, row.Changes.Count);
        double defects = DefectDensity(row);
        double buildFailure = IsFailedBuild(row.BuildResult) ? 1 : 0;
        double recency = Recency(lastChanged, nowUtc);
        double uncertainty = IsUncertain(row) ? 1 : 0;

        ScoreComponent[] components =
        [
            new("churn", churn, _risk.ChurnWeight, churn * _risk.ChurnWeight),
            new("defects", defects, _risk.DefectWeight, defects * _risk.DefectWeight),
            new("buildFailure", buildFailure, _risk.BuildFailureWeight, buildFailure * _risk.BuildFailureWeight),
            new("recency", recency, _risk.RecencyWeight, recency * _risk.RecencyWeight),
            new("uncertainty", uncertainty, _risk.UncertaintyWeight, uncertainty * _risk.UncertaintyWeight),
        ];

        double score = Math.Clamp(components.Sum(c => c.Weighted), 0, 1);

        // Lines added/deleted and author count are not carried by SubsystemRow; see §2.6. They stay zero
        // rather than being approximated, and no term in the score depends on them.
        var churnMetrics = new ChurnMetrics(
            LinesAdded: 0, LinesDeleted: 0, FilesTouched: row.TotalFilesModified,
            CommitCount: row.Changes.Count, DistinctAuthorCount: 0, LastChangedUtc: lastChanged);

        return new RegressionRiskAssessment(score, Band(score), churnMetrics, components);
    }

    private double ChurnMagnitude(int filesTouched, int changeCount)
    {
        double reference = Math.Log(1 + _risk.ChurnReferenceFiles) + Math.Log(1 + _risk.ChurnReferenceChanges);
        if (reference <= 0)
        {
            return 0;
        }

        double observed = Math.Log(1 + Math.Max(0, filesTouched)) + Math.Log(1 + Math.Max(0, changeCount));
        return Math.Min(1, observed / reference);
    }

    private double DefectDensity(SubsystemRow row)
    {
        if (_risk.DefectSaturation <= 0)
        {
            return 0;
        }

        // Distinct by id: one work item linked to five commits is one defect, not five.
        var byId = new Dictionary<int, RegressionWorkItemKind>();
        foreach (RegressionChangeRef change in row.Changes)
        {
            foreach (RegressionWorkItemRef workItem in change.WorkItems)
            {
                byId[workItem.Id] = workItem.Kind;
            }
        }

        int bugs = byId.Values.Count(k => k == RegressionWorkItemKind.Bug);
        int incidents = byId.Values.Count(k => k == RegressionWorkItemKind.Ims);

        // Incidents weigh double: an IMS is a customer-observed escape, the exact failure regression prevents.
        return Math.Min(1, (bugs + (2.0 * incidents)) / _risk.DefectSaturation);
    }

    private double Recency(DateTimeOffset lastChanged, DateTimeOffset nowUtc)
    {
        if (lastChanged == DateTimeOffset.UnixEpoch || _risk.RecencyHalfLifeDays <= 0)
        {
            return 0;
        }

        double days = (nowUtc - lastChanged).TotalDays;
        return days <= 0 ? 1 : Math.Pow(2, -days / _risk.RecencyHalfLifeDays);
    }

    private static bool IsFailedBuild(string? buildResult)
        => !string.IsNullOrWhiteSpace(buildResult)
           && !buildResult.Equals(SucceededResult, StringComparison.OrdinalIgnoreCase);

    private static bool IsUncertain(SubsystemRow row)
        => row.RegressionAreas is null or { Count: 0 }
           || row.Category == RegressionCategoryKind.Unclassified;

    private RiskTier Band(double score)
    {
        if (score >= _risk.CriticalThreshold)
        {
            return RiskTier.Critical;
        }

        return score >= _risk.HighThreshold ? RiskTier.High : RiskTier.Medium;
    }
}
