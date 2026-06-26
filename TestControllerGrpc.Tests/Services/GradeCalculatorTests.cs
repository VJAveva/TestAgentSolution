using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for <see cref="GradeCalculator"/>: deterministic letter-grade
/// computation from pass rate and penalties using default config weights
/// (A≥97, B≥93, C≥88, D≥80, else F).
/// </summary>
public class GradeCalculatorTests
{
    private static GradeCalculator NewCalculator() => new(new BuildReportCardConfig());

    [Fact]
    public void Compute_Should_ReturnGradeA_When_PerfectPassRateAndNoPenalty()
    {
        var result = NewCalculator().Compute(100.0, 0, 0, 0, 0);

        Assert.Equal("A", result.Letter);
        Assert.Equal(100.0, result.Score);
        Assert.Equal("Ship it", result.Verdict);
        Assert.Equal(ReportSeverity.Pass, result.Severity);
    }

    [Fact]
    public void Compute_Should_ReturnGradeB_When_PassRateInBRange()
    {
        var result = NewCalculator().Compute(95.0, 0, 0, 0, 0);

        Assert.Equal("B", result.Letter);
        Assert.Equal(ReportSeverity.Warn, result.Severity);
        Assert.Equal("Ship with caveats", result.Verdict);
    }

    [Fact]
    public void Compute_Should_ReturnGradeC_When_PassRateInCRange()
    {
        var result = NewCalculator().Compute(90.0, 0, 0, 0, 0);

        Assert.Equal("C", result.Letter);
        Assert.Equal(ReportSeverity.Warn, result.Severity);
        Assert.Equal("Review required", result.Verdict);
    }

    [Fact]
    public void Compute_Should_ReturnGradeD_When_PassRateInDRange()
    {
        var result = NewCalculator().Compute(85.0, 0, 0, 0, 0);

        Assert.Equal("D", result.Letter);
        Assert.Equal(ReportSeverity.Fail, result.Severity);
        Assert.Equal("Do not ship", result.Verdict);
    }

    [Fact]
    public void Compute_Should_ReturnGradeF_When_PassRateBelowDThreshold()
    {
        var result = NewCalculator().Compute(70.0, 0, 0, 0, 0);

        Assert.Equal("F", result.Letter);
        Assert.Equal(ReportSeverity.Fail, result.Severity);
    }

    [Fact]
    public void Compute_Should_ApplyRegressionPenalty_When_RegressionsPresent()
    {
        // 100% - (2 regressions * 2.0) = 96 → B
        var result = NewCalculator().Compute(100.0, 2, 0, 0, 0);

        Assert.Equal(96.0, result.Score);
        Assert.Equal("B", result.Letter);
        Assert.Contains(result.BreakdownLines, l => l.Contains("Regressions (2)"));
    }

    [Fact]
    public void Compute_Should_FloorScoreAtZero_When_PenaltiesExceedPassRate()
    {
        // 1% - (10 regressions * 2.0) = -19 → floored to 0 → F
        var result = NewCalculator().Compute(1.0, 10, 0, 0, 0);

        Assert.Equal(0.0, result.Score);
        Assert.Equal("F", result.Letter);
    }

    [Fact]
    public void Compute_Should_IncludeAllPenaltyLines_When_AllPenaltyTypesPresent()
    {
        var result = NewCalculator().Compute(100.0, 1, 1, 1, 1);

        Assert.Contains(result.BreakdownLines, l => l.Contains("Regressions"));
        Assert.Contains(result.BreakdownLines, l => l.Contains("PSR failures"));
        Assert.Contains(result.BreakdownLines, l => l.Contains("PSR warnings"));
        Assert.Contains(result.BreakdownLines, l => l.Contains("CIs below 90%"));
    }
}
