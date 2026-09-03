using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Learning;

namespace TestController.WebApi.Tests.Impact;

public sealed class FeatureVectorExtractorTests
{
    private static CalibrationFeatures Sample(AnchorSource? anchor = AnchorSource.HistoricalFailure, bool automation = true)
        => new(
            Bm25Score: 0.1, DenseScore: 0.2, RrfScore: 0.3, FeatureScore: 0.4, FanOutPenalty: 0.5,
            LlmGrade: 3, LlmConfidence: 0.9, CitedSignalCount: 2,
            AnchorWeight: 0.7, AnchorSource: anchor,
            HistoricalFailureRate: 0.15, DaysSinceLastRun: 5, AutomationStatusFlag: automation,
            SameSubsystemFlag: true, AreaPathOverlapDepth: 2, TestCaseAgeDays: 100, ParentChildCount: 12);

    [Fact]
    public void Extract_Should_ProduceFixedLengthVector()
    {
        Assert.Equal(FeatureVectorExtractor.FeatureCount, new FeatureVectorExtractor().Extract(Sample()).Length);
    }

    [Fact]
    public void FeatureNames_Should_MatchFeatureCount()
    {
        Assert.Equal(FeatureVectorExtractor.FeatureCount, FeatureVectorExtractor.FeatureNames.Count);
    }

    [Fact]
    public void Extract_Should_OneHotEncodeAnchorSource()
    {
        float[] vector = new FeatureVectorExtractor().Extract(Sample(anchor: AnchorSource.HistoricalFailure));

        Assert.Equal(1f, vector[10]); // anchorHistoricalFailure
        Assert.Equal(0f, vector[9]);  // anchorLinkedWorkItem
        Assert.Equal(0f, vector[11]); // anchorDeclaredMapping
        Assert.Equal(0f, vector[12]); // anchorPriorSelection
    }

    [Fact]
    public void Extract_Should_ZeroAnchorOneHot_When_Null()
    {
        float[] vector = new FeatureVectorExtractor().Extract(Sample(anchor: null));

        Assert.Equal(0f, vector[9]);
        Assert.Equal(0f, vector[10]);
        Assert.Equal(0f, vector[11]);
        Assert.Equal(0f, vector[12]);
    }

    [Fact]
    public void Extract_Should_EncodeAutomationFlag()
    {
        Assert.Equal(1f, new FeatureVectorExtractor().Extract(Sample(automation: true))[15]);
        Assert.Equal(0f, new FeatureVectorExtractor().Extract(Sample(automation: false))[15]);
    }
}
