using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class BuildResultsAggregatorTests
{
    // ???????????????????????????????????????????????????????????????????
    // EvaluateHealth
    // ???????????????????????????????????????????????????????????????????

    [Theory]
    [InlineData(100.0, HealthStatus.Good)]
    [InlineData(96.0, HealthStatus.Good)]
    [InlineData(95.1, HealthStatus.Good)]
    [InlineData(95.0, HealthStatus.Warning)]  // boundary: exactly at good threshold is NOT > 95
    [InlineData(90.0, HealthStatus.Warning)]
    [InlineData(85.0, HealthStatus.Warning)]  // boundary: exactly at warning threshold
    [InlineData(84.9, HealthStatus.Bad)]
    [InlineData(50.0, HealthStatus.Bad)]
    [InlineData(0.0, HealthStatus.Bad)]
    public void EvaluateHealth_Should_ReturnCorrectStatus_When_GivenPassRate(double passRate, HealthStatus expected)
    {
        var config = new BuildResultsConfig { GoodThreshold = 95.0, WarningThreshold = 85.0 };
        var aggregator = new BuildResultsAggregator(config);

        var result = aggregator.EvaluateHealth(passRate);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void EvaluateHealth_Should_UseCustomThresholds_When_ConfigOverridden()
    {
        var config = new BuildResultsConfig { GoodThreshold = 80.0, WarningThreshold = 60.0 };
        var aggregator = new BuildResultsAggregator(config);

        Assert.Equal(HealthStatus.Good, aggregator.EvaluateHealth(81.0));
        Assert.Equal(HealthStatus.Warning, aggregator.EvaluateHealth(70.0));
        Assert.Equal(HealthStatus.Bad, aggregator.EvaluateHealth(59.0));
    }

    // ???????????????????????????????????????????????????????????????????
    // EvaluateBuildHealth
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void EvaluateBuildHealth_Should_ReturnGood_When_AllTestsPass()
    {
        var config = new BuildResultsConfig { GoodThreshold = 95.0, WarningThreshold = 85.0 };
        var aggregator = new BuildResultsAggregator(config);

        var node = new BuildNode
        {
            BuildNumber = "1.0",
            TotalTests = 100,
            PassedTests = 100,
            PassRate = 100.0,
            UseCases =
            [
                new UseCaseNode
                {
                    UseCaseName = "UC1",
                    Total = 100,
                    Passed = 100,
                    PassRate = 100.0,
                    TestResults = Enumerable.Range(0, 100)
                        .Select(i => new TestResult { TestName = $"Test{i}", Outcome = "Passed" })
                        .ToList()
                }
            ]
        };

        var result = aggregator.EvaluateBuildHealth(node);

        Assert.Equal(HealthStatus.Good, result.Health);
        Assert.Equal(100, result.TotalTests);
        Assert.Equal(100, result.PassedTests);
        Assert.Equal(100.0, result.PassRate);
    }

    [Fact]
    public void EvaluateBuildHealth_Should_ReturnBad_When_ManyTestsFail()
    {
        var config = new BuildResultsConfig { GoodThreshold = 95.0, WarningThreshold = 85.0 };
        var aggregator = new BuildResultsAggregator(config);

        var node = new BuildNode
        {
            TotalTests = 100,
            PassedTests = 50,
            FailedTests = 50,
            PassRate = 50.0,
            UseCases =
            [
                new UseCaseNode
                {
                    Total = 100, Passed = 50, Failed = 50,
                    PassRate = 50.0,
                    TestResults = []
                }
            ]
        };

        var result = aggregator.EvaluateBuildHealth(node);

        Assert.Equal(HealthStatus.Bad, result.Health);
    }

    [Fact]
    public void EvaluateBuildHealth_Should_HandleZeroTests_When_NoUseCases()
    {
        var config = new BuildResultsConfig();
        var aggregator = new BuildResultsAggregator(config);

        var node = new BuildNode { UseCases = [], TotalTests = 0, PassRate = 0.0 };
        var result = aggregator.EvaluateBuildHealth(node);

        Assert.Equal(0, result.TotalTests);
        Assert.Equal(0.0, result.PassRate);
        Assert.Equal(HealthStatus.Bad, result.Health);
    }

    [Fact]
    public void EvaluateBuildHealth_Should_NotMutateOriginal_When_Evaluating()
    {
        var config = new BuildResultsConfig();
        var aggregator = new BuildResultsAggregator(config);

        var original = new BuildNode
        {
            BuildNumber = "orig",
            Health = HealthStatus.Unknown,
            TotalTests = 10,
            PassedTests = 10,
            PassRate = 100.0,
            UseCases = [new UseCaseNode { Total = 10, Passed = 10, PassRate = 100.0, TestResults = [] }]
        };

        var result = aggregator.EvaluateBuildHealth(original);

        Assert.Equal(HealthStatus.Unknown, original.Health);
        Assert.Equal(HealthStatus.Good, result.Health);
        Assert.Equal("orig", result.BuildNumber);
    }
}
