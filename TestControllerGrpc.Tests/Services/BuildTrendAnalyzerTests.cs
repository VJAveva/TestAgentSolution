using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for BuildTrendAnalyzer: grouping builds by week/month
/// and computing trend reports.
/// </summary>
public class BuildTrendAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;

    public BuildTrendAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TrendTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _parser = new TrxResultsParser();
        _config = new BuildResultsConfig { GoodThreshold = 95, WarningThreshold = 85 };
        _aggregator = new BuildResultsAggregator(_config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateBuild(string buildNumber, int passed, int failed)
    {
        var buildDir = Path.Combine(_tempDir, buildNumber);
        Directory.CreateDirectory(buildDir);
        var ucDir = Path.Combine(buildDir, "Smoke");
        Directory.CreateDirectory(ucDir);

        var results = new List<string>();
        for (int i = 0; i < passed; i++)
            results.Add($@"<UnitTestResult testName=""Test{i}"" outcome=""Passed"" duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}""/>");
        for (int i = 0; i < failed; i++)
            results.Add($@"<UnitTestResult testName=""FailTest{i}"" outcome=""Failed"" duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}""/>");

        var trx = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    {string.Join("\n    ", results)}
  </Results>
</TestRun>";
        File.WriteAllText(Path.Combine(ucDir, "results.trx"), trx);
    }

    [Fact]
    public void AnalyzeTrends_Should_ReturnEmptyReport_When_NoBuilds()
    {
        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        Assert.NotNull(report);
        Assert.Empty(report.Builds);
        Assert.Empty(report.WeeklySummaries);
        Assert.Empty(report.MonthlySummaries);
    }

    [Fact]
    public void AnalyzeTrends_Should_ReturnBuilds_When_DataExists()
    {
        CreateBuild("Build_1", passed: 10, failed: 0);
        CreateBuild("Build_2", passed: 8, failed: 2);

        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        Assert.Equal(2, report.Builds.Count);
    }

    [Fact]
    public void AnalyzeTrends_Should_ComputePassRate_When_BuildsExist()
    {
        CreateBuild("Build_1", passed: 10, failed: 0);

        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        Assert.Single(report.Builds);
        Assert.Equal(100.0, report.Builds[0].PassRate);
        Assert.Equal(10, report.Builds[0].TotalTests);
    }

    [Fact]
    public void AnalyzeTrends_Should_GroupByWeek_When_MultipleBuilds()
    {
        CreateBuild("Build_1", passed: 10, failed: 0);
        CreateBuild("Build_2", passed: 8, failed: 2);

        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        // Both builds are from the same week
        Assert.True(report.WeeklySummaries.Count >= 1);
        var week = report.WeeklySummaries[0];
        Assert.True(week.BuildCount >= 2);
        Assert.True(week.TotalTests > 0);
    }

    [Fact]
    public void AnalyzeTrends_Should_GroupByMonth_When_DataExists()
    {
        CreateBuild("Build_1", passed: 10, failed: 0);

        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        Assert.True(report.MonthlySummaries.Count >= 1);
    }

    [Fact]
    public void AnalyzeTrends_Should_IncludeThresholds_When_ReportGenerated()
    {
        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var report = analyzer.AnalyzeTrends(_tempDir, _parser);

        Assert.Equal(95.0, report.GoodThreshold);
        Assert.Equal(85.0, report.WarningThreshold);
    }

    [Fact]
    public void AnalyzePerUseCaseTrends_Should_ReturnEmpty_When_NoBuilds()
    {
        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var trends = analyzer.AnalyzePerUseCaseTrends(_tempDir, _parser);

        Assert.Empty(trends);
    }

    [Fact]
    public void AnalyzePerUseCaseTrends_Should_GroupByUseCase_When_DataExists()
    {
        CreateBuild("Build_1", passed: 10, failed: 0);

        var analyzer = new BuildTrendAnalyzer(_aggregator, _config);
        var trends = analyzer.AnalyzePerUseCaseTrends(_tempDir, _parser);

        Assert.True(trends.Count >= 1);
        Assert.Contains("Smoke", trends.Keys);
    }

    // ── DTO default tests ────────────────────────────────────────────

    [Fact]
    public void BuildTrendEntry_Should_HaveCorrectDefaults()
    {
        var entry = new BuildTrendEntry();
        Assert.Empty(entry.BuildNumber);
        Assert.Equal(0, entry.TotalTests);
        Assert.Equal(0.0, entry.PassRate);
    }

    [Fact]
    public void TrendReport_Should_HaveCorrectDefaults()
    {
        var report = new TrendReport();
        Assert.Empty(report.Builds);
        Assert.Empty(report.WeeklySummaries);
        Assert.Empty(report.MonthlySummaries);
    }

    [Fact]
    public void PeriodSummary_Should_HaveCorrectDefaults()
    {
        var summary = new PeriodSummary();
        Assert.Empty(summary.Period);
        Assert.Equal(0, summary.BuildCount);
    }

    [Fact]
    public void UseCaseTrendEntry_Should_HaveCorrectDefaults()
    {
        var entry = new UseCaseTrendEntry();
        Assert.Empty(entry.BuildNumber);
        Assert.Equal(0, entry.Total);
    }
}
