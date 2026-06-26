using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Integration tests for <see cref="BuildReportAggregator"/>: reads TRX files
/// from a temp ReportResults root (one CI per top-level folder, agent
/// subfolders consolidated) and produces a single <see cref="BuildReportCard"/>.
/// PSR cards are dummy until the Excel format is finalised.
/// </summary>
public class BuildReportAggregatorTests : IDisposable
{
    private readonly string _root;
    private readonly TrxResultsParser _parser = new();

    private const string BuildName = "lkf_main_20260619.4";

    public BuildReportAggregatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ReportCard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void WriteTrx(string ci, string agent, params (string testName, string outcome)[] tests)
    {
        var dir = Path.Combine(_root, BuildName, ci, agent);
        Directory.CreateDirectory(dir);

        var entries = tests.Select(t =>
            $@"<UnitTestResult testName=""{t.testName}"" outcome=""{t.outcome}""
                duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}"">
                {(t.outcome == "Failed" ? $"<Output><ErrorInfo><Message>{t.testName} failed</Message></ErrorInfo></Output>" : "")}
               </UnitTestResult>");

        var trx = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    {string.Join("\n    ", entries)}
  </Results>
</TestRun>";
        File.WriteAllText(Path.Combine(dir, "results.trx"), trx);
    }

    private BuildReportAggregator NewAggregator(BuildReportCardConfig config)
    {
        var logger = new Mock<IAppLogger>().Object;
        var grader = new GradeCalculator(config);
        var owners = new CiOwnerResolver(config);
        var summaryStore = new BuildSummaryStore(config, logger);
        var patternAnalyzer = new FailurePatternAnalyzer(_parser, new BuildResultsConfig());
        return new BuildReportAggregator(_parser, grader, owners, summaryStore, patternAnalyzer, config, logger);
    }

    private BuildReportCardConfig NewConfig() => new()
    {
        ReportResultsRoot = _root,
        SummaryStorePath = Path.Combine(_root, "summaries.json"),
    };

    [Fact]
    public void Aggregate_Should_ConsolidateCisAndAgents_When_TrxFilesPresent()
    {
        WriteTrx("SmokeTestResults", "Agent01", ("TestA", "Passed"), ("TestB", "Passed"));
        WriteTrx("SmokeTestResults", "Agent02", ("TestC", "Failed"));
        WriteTrx("LegacyCI", "Agent01", ("TestD", "Passed"));

        var card = NewAggregator(NewConfig()).Aggregate();

        Assert.True(card.HasData);
        Assert.Equal(2, card.Cis.Count);
        Assert.Equal(4, card.TotalTests);
        Assert.Equal(3, card.PassedTests);
        Assert.Equal(1, card.FailedTests);
        // One execution-unit row per use-case subfolder per CI (not merged across CIs).
        Assert.Equal(3, card.Agents.Count);
    }

    [Fact]
    public void Aggregate_Should_ProduceSingleFailureEntry_When_OneTestFails()
    {
        WriteTrx("SmokeTestResults", "Agent01", ("TestA", "Passed"));
        WriteTrx("SmokeTestResults", "Agent02", ("TestC", "Failed"));

        var card = NewAggregator(NewConfig()).Aggregate();

        Assert.Single(card.Failures);
        Assert.Equal("TestC", card.Failures[0].TestName);
    }

    [Fact]
    public void Aggregate_Should_ReturnDummyPsrCards_Always()
    {
        WriteTrx("SmokeTestResults", "Agent01", ("TestA", "Passed"));

        var card = NewAggregator(NewConfig()).Aggregate();

        Assert.Equal(5, card.Psrs.Count);
        Assert.All(card.Psrs, p => Assert.Equal(PsrOutcome.Pending, p.Outcome));
    }

    [Fact]
    public void Aggregate_Should_ReturnEmptyCard_When_RootMissing()
    {
        var config = new BuildReportCardConfig
        {
            ReportResultsRoot = Path.Combine(_root, "does-not-exist"),
            SummaryStorePath = Path.Combine(_root, "summaries.json"),
        };

        var card = NewAggregator(config).Aggregate();

        Assert.False(card.HasData);
        Assert.Empty(card.Cis);
        Assert.Equal(5, card.Psrs.Count); // dummy PSR still present
    }
}
