using TestControllerGrpc.Core.Impact.Eval;

namespace TestController.WebApi.Tests.Impact;

public sealed class EvalMetricsTests
{
    [Fact]
    public void RecallAtK_Should_MeasureGroundTruthInTopK()
    {
        Assert.Equal(1.0 / 3, EvalMetrics.RecallAtK([1, 2, 3], [1, 4, 2, 5], k: 2), 10);
    }

    [Fact]
    public void PrecisionAtK_Should_MeasureRelevantInTopK()
    {
        Assert.Equal(0.5, EvalMetrics.PrecisionAtK([1, 2, 3], [1, 4, 2, 5], k: 2));
    }

    [Fact]
    public void IsSafeRecall_Should_BeTrue_When_AllFailedSelected()
    {
        Assert.True(EvalMetrics.IsSafeRecall([1, 2], [1, 2, 3]));
        Assert.False(EvalMetrics.IsSafeRecall([1, 2], [1, 3]));
    }

    [Fact]
    public void Apfd_Should_MatchKnownExample()
    {
        // 5 tests, one fault first revealed at position 2 -> 1 - 2/5 + 1/10 = 0.7.
        Assert.Equal(0.7, EvalMetrics.Apfd([10, 20, 30, 40, 50], [20]), 10);
    }

    [Fact]
    public void Cost_Should_BeSelectedOverFullSuite()
    {
        Assert.Equal(0.3, EvalMetrics.Cost(30, 100), 10);
    }

    [Fact]
    public void Aggregate_Should_ComputeSafeRecallRate()
    {
        var cases = new List<EvalCase>
        {
            new("A", [1, 2, 3], [1, 2], [1, 2], SelectedRuntimeSeconds: 30, FullSuiteRuntimeSeconds: 100, EarlyExit: false),
            new("B", [4], [4, 5], [5], SelectedRuntimeSeconds: 10, FullSuiteRuntimeSeconds: 100, EarlyExit: true),
        };

        EvalReport report = EvalMetrics.Aggregate(cases, [10]);

        Assert.Equal(2, report.Cases);
        Assert.Equal(0.5, report.SafeRecall); // area A safe (1,2 selected), area B unsafe (5 missing)
        Assert.Equal(0.5, report.EarlyExitRate);
    }
}
