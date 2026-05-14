using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for FlakyTestDetector: identifies intermittent test failures
/// across recent builds.
/// </summary>
public class FlakyTestDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TrxResultsParser _parser;

    public FlakyTestDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FlakyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _parser = new TrxResultsParser();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateBuild(string buildNumber, params (string useCase, string testName, string outcome)[] tests)
    {
        var buildDir = Path.Combine(_tempDir, buildNumber);
        Directory.CreateDirectory(buildDir);

        var grouped = tests.GroupBy(t => t.useCase);
        foreach (var ucGroup in grouped)
        {
            var ucDir = Path.Combine(buildDir, ucGroup.Key);
            Directory.CreateDirectory(ucDir);

            var testEntries = ucGroup.Select(t =>
                $@"<UnitTestResult testName=""{t.testName}"" outcome=""{t.outcome}"" 
                    duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}"">
                    {(t.outcome == "Failed" ? $"<Output><ErrorInfo><Message>{t.testName} failed</Message></ErrorInfo></Output>" : "")}
                   </UnitTestResult>");

            var trx = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    {string.Join("\n    ", testEntries)}
  </Results>
</TestRun>";
            File.WriteAllText(Path.Combine(ucDir, "results.trx"), trx);
        }
    }

    [Fact]
    public void DetectFlakyTests_Should_ReturnEmpty_When_NoBuilds()
    {
        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser);
        Assert.Empty(results);
    }

    [Fact]
    public void DetectFlakyTests_Should_ReturnEmpty_When_AllTestsPass()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Passed"), ("UC1", "TestB", "Passed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"), ("UC1", "TestB", "Passed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Empty(results);
    }

    [Fact]
    public void DetectFlakyTests_Should_DetectFlaky_When_TestFailsIntermittently()
    {
        // TestA fails in builds 1 and 3 but passes in 2
        CreateBuild("Build1", ("UC1", "TestA", "Failed"), ("UC1", "TestB", "Passed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"), ("UC1", "TestB", "Passed"));
        CreateBuild("Build3", ("UC1", "TestA", "Failed"), ("UC1", "TestB", "Passed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Equal("TestA", results[0].TestName);
        Assert.Equal(2, results[0].FailureCount);
        Assert.Equal(3, results[0].TotalRuns);
    }

    [Fact]
    public void DetectFlakyTests_Should_ClassifyConsistent_When_FailsInAllBuilds()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Failed"));
        CreateBuild("Build3", ("UC1", "TestA", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Equal("Consistent", results[0].Classification);
        Assert.Equal(100.0, results[0].FlakyRate);
    }

    [Fact]
    public void DetectFlakyTests_Should_ClassifyFrequent_When_FailsInMostBuilds()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"));
        CreateBuild("Build3", ("UC1", "TestA", "Failed"));
        CreateBuild("Build4", ("UC1", "TestA", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Equal("Frequent", results[0].Classification); // 3/4 = 75% ≥ 50%
    }

    [Fact]
    public void DetectFlakyTests_Should_IgnoreTests_When_BelowMinFailures()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Empty(results); // Only 1 failure < minFailures(2)
    }

    [Fact]
    public void DetectFlakyTests_Should_SortByFlakyRate_When_MultipleFlaky()
    {
        // TestA: 2/3 fails, TestB: 3/3 fails
        CreateBuild("Build1", ("UC1", "TestA", "Failed"), ("UC1", "TestB", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"), ("UC1", "TestB", "Failed"));
        CreateBuild("Build3", ("UC1", "TestA", "Failed"), ("UC1", "TestB", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Equal(2, results.Count);
        // TestB (100%) should come before TestA (~67%)
        Assert.Equal("TestB", results[0].TestName);
    }

    [Fact]
    public void DetectFlakyTests_Should_TrackUseCaseName_When_TestBelongsToUseCase()
    {
        CreateBuild("Build1", ("LoginSuite", "LoginTest", "Failed"));
        CreateBuild("Build2", ("LoginSuite", "LoginTest", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Equal("LoginSuite", results[0].UseCaseName);
    }

    [Fact]
    public void DetectFlakyTests_Should_TrackLastError_When_TestHasErrorMessage()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Contains("TestA failed", results[0].LastError);
    }

    [Fact]
    public void DetectFlakyTests_Should_ListFailedBuilds_When_Detected()
    {
        CreateBuild("Build1", ("UC1", "TestA", "Failed"));
        CreateBuild("Build2", ("UC1", "TestA", "Passed"));
        CreateBuild("Build3", ("UC1", "TestA", "Failed"));

        var detector = new FlakyTestDetector();
        var results = detector.DetectFlakyTests(_tempDir, _parser, recentBuilds: 5, minFailures: 2);

        Assert.Single(results);
        Assert.Equal(2, results[0].FailedInBuilds.Count);
    }

    [Fact]
    public void FlakyTestAlert_Should_HaveCorrectDefaults()
    {
        var alert = new FlakyTestAlert();

        Assert.Empty(alert.TestName);
        Assert.Empty(alert.UseCaseName);
        Assert.Equal(0, alert.FailureCount);
        Assert.Equal(0, alert.TotalRuns);
        Assert.Empty(alert.FailedInBuilds);
        Assert.Empty(alert.LastError);
        Assert.Empty(alert.Classification);
    }
}
