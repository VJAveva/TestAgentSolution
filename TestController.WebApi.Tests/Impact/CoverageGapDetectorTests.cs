using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Selection;

namespace TestController.WebApi.Tests.Impact;

public sealed class CoverageGapDetectorTests
{
    private static ImpactedArea Area(RiskTier tier = RiskTier.Medium, IReadOnlyList<string>? declared = null)
        => new("A", "MyArea", "Sub", "Vob", [], declared ?? [], tier, new ChurnMetrics(0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch));

    private static Scored<FeatureCandidate> Feat(int id)
        => new(new FeatureCandidate(new AdoWorkItemRef(id, "Feature", $"F{id}", null, "Active", 1), "d", FeatureDiscoveryPath.DirectFeatureSearch, [], 0), 1.0, []);

    private static MappedTestCase M(int tcId, int featureId, MappingConfidence confidence = MappingConfidence.Observed)
        => new(
            new TestCaseCandidate(new AdoWorkItemRef(tcId, "Test Case", "TC", null, "Design", 1), "s", "Automated", featureId, []),
            featureId, 0.5, confidence, null, null, []);

    [Fact]
    public void Detect_Should_FlagUncoveredSelectedFeature()
    {
        IReadOnlyList<CoverageGap> gaps = new CoverageGapDetector()
            .Detect(Area(), [M(101, 900)], [Feat(900), Feat(901)]);

        Assert.Contains(gaps, g => g.RegressionArea == "F901");
    }

    [Fact]
    public void Detect_Should_Flag_When_EntireSelectionAssumed()
    {
        IReadOnlyList<CoverageGap> gaps = new CoverageGapDetector()
            .Detect(Area(RiskTier.Medium), [M(101, 900, MappingConfidence.Assumed), M(102, 900, MappingConfidence.Assumed), M(103, 900, MappingConfidence.Assumed)], [Feat(900)]);

        Assert.Contains(gaps, g => g.Reason == "no evidenced mapping");
    }

    [Fact]
    public void Detect_Should_FlagHighRiskUnderCoverage()
    {
        IReadOnlyList<CoverageGap> gaps = new CoverageGapDetector()
            .Detect(Area(RiskTier.High), [M(101, 900)], [Feat(900)]);

        Assert.Contains(gaps, g => g.Reason.Contains("risk area"));
    }

    [Fact]
    public void Detect_Should_FlagDeclaredAreas_When_NothingSelected()
    {
        IReadOnlyList<CoverageGap> gaps = new CoverageGapDetector()
            .Detect(Area(RiskTier.High, ["A1", "A2"]), [], []);

        Assert.Contains(gaps, g => g.RegressionArea == "A1");
        Assert.Contains(gaps, g => g.RegressionArea == "A2");
    }

    [Fact]
    public void Detect_Should_CarryAreaRiskTier()
    {
        IReadOnlyList<CoverageGap> gaps = new CoverageGapDetector()
            .Detect(Area(RiskTier.Critical), [], [Feat(900)]);

        Assert.All(gaps, g => Assert.Equal(RiskTier.Critical, g.RiskTier));
    }
}
