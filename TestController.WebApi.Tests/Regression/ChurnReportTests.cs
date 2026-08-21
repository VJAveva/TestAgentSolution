using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Unit tests for the offline churn summarizer + CSV/HTML report builder (Regression tab export/email).
/// Pure functions over <see cref="SubsystemRow"/>, so no host/ADO wiring is needed.
/// </summary>
public class ChurnReportTests
{
    private static RegressionWorkItemRef WorkItem(int id, RegressionWorkItemKind kind) =>
        new(id, kind, $"WI {id}", null);

    private static RegressionChangeRef Change(string summary, RegressionChangeKind kind, params RegressionWorkItemRef[] wi) =>
        new(Guid.NewGuid().ToString("N"), summary, DateTimeOffset.UtcNow, [], wi, kind);

    private static SubsystemRow Row(string comp, RegressionCategoryKind cat, int files, string risk = "succeeded", params RegressionChangeRef[] changes) =>
        new(
            Component: comp,
            Subsystem: comp,
            Category: cat,
            CategoryConfidence: RegressionEvidenceKind.Declared,
            FilesModified: [],
            TotalFilesModified: files,
            Changes: changes,
            RiskTier: risk,
            AutomatedSuites: [],
            ManualSuites: [],
            EstimatedMinutes: 0,
            IsEstimate: true);

    private static ChurnReport Report(params SubsystemRow[] rows) =>
        new("Weekly", "2026-08-15 \u2013 2026-08-21", new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 21), DateTimeOffset.UtcNow, rows);

    [Fact]
    public void Summarize_Should_ReportComponentChangeAndFileCounts_When_RowsPresent()
    {
        var report = Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 5, changes: Change("Merged PR 1", RegressionChangeKind.PullRequest)),
            Row("Beta", RegressionCategoryKind.Config, 3, changes: Change("fix", RegressionChangeKind.Commit)));

        var summary = new ChurnSummarizer().Summarize(report);

        Assert.Contains("2 component(s)", summary.Headline);
        Assert.Contains("2 change(s)", summary.Headline);
        Assert.Contains("8 file(s)", summary.Headline);
        Assert.Contains(summary.Highlights, h => h.Contains("Runtime") && h.Contains("Config"));
    }

    [Fact]
    public void Summarize_Should_HighlightBugs_When_WorkItemsLinked()
    {
        var report = Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 2, changes:
                Change("fix crash", RegressionChangeKind.PullRequest, WorkItem(101, RegressionWorkItemKind.Bug))));

        var summary = new ChurnSummarizer().Summarize(report);

        Assert.Contains(summary.Highlights, h => h.Contains("1 bug(s)"));
    }

    [Fact]
    public void Summarize_Should_FlagNonSuccessfulBuild_When_RiskNotSucceeded()
    {
        var report = Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 2, risk: "failed", changes: Change("x", RegressionChangeKind.Commit)));

        var summary = new ChurnSummarizer().Summarize(report);

        Assert.Contains(summary.Highlights, h => h.Contains("did not succeed") && h.Contains("Alpha"));
    }

    [Fact]
    public void Summarize_Should_ReturnEmptyMessage_When_NoRows()
    {
        var summary = new ChurnSummarizer().Summarize(Report());

        Assert.Contains("No component changes", summary.Headline);
        Assert.Empty(summary.Highlights);
    }

    [Fact]
    public void BuildCsv_Should_EmitHeaderAndOneLinePerComponent()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());

        var csv = builder.BuildCsv(Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 5, changes: Change("x", RegressionChangeKind.PullRequest)),
            Row("Beta", RegressionCategoryKind.Config, 3)));

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#,Component,Category", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Contains("Alpha", lines[1]);
    }

    [Fact]
    public void BuildCsv_Should_QuoteAndEscapeField_When_SummaryContainsCommaAndQuote()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());

        var csv = builder.BuildCsv(Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 1, changes: Change("fix a, b and \"c\"", RegressionChangeKind.Commit))));

        Assert.Contains("\"fix a, b and \"\"c\"\"\"", csv);
    }

    [Fact]
    public void BuildHtml_Should_ContainSummaryAndComponentRow()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());

        var html = builder.BuildHtml(Report(
            Row("Alpha", RegressionCategoryKind.Runtime, 5, changes: Change("Merged PR", RegressionChangeKind.PullRequest))));

        Assert.Contains("AI Summary", html);
        Assert.Contains("Alpha", html);
        Assert.Contains("<table", html);
    }

    [Fact]
    public void BuildHtml_Should_HtmlEncode_When_ComponentContainsMarkup()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());

        var html = builder.BuildHtml(Report(
            Row("<script>", RegressionCategoryKind.Runtime, 1, changes: Change("x", RegressionChangeKind.Commit))));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void SummarizeComponent_Should_ListFunctionalTests_When_UseCasesPresent()
    {
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 4, changes: Change("Merged PR", RegressionChangeKind.PullRequest))
            with { UseCases = ["Login flow", "Alarm ack"], RegressionAreas = ["Security"] };

        var text = new ChurnSummarizer().SummarizeComponent(row);

        Assert.Contains("Alpha", text);
        Assert.Contains("1 PR", text);
        Assert.Contains("Run functional tests:", text);
        Assert.Contains("Login flow", text);
        Assert.Contains("Security", text);
    }

    [Fact]
    public void SummarizeComponent_Should_FlagFailingBuild_When_RiskNotSucceeded()
    {
        var row = Row("Beta", RegressionCategoryKind.Config, 2, risk: "failed", changes: Change("x", RegressionChangeKind.Commit));

        var text = new ChurnSummarizer().SummarizeComponent(row);

        Assert.Contains("verify before testing", text);
    }

    [Fact]
    public void BuildCsv_Should_IncludeImpactedFunctionalityAndTestUseCases()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 2, changes: Change("x", RegressionChangeKind.PullRequest))
            with { RegressionAreas = ["Alarming", "Trends"], UseCases = ["UC-1 Ack alarm"] };

        var csv = builder.BuildCsv(Report(row));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("ImpactedFunctionality", lines[0]);
        Assert.Contains("TestUseCases", lines[0]);
        Assert.Contains("Alarming; Trends", csv);
        Assert.Contains("UC-1 Ack alarm", csv);
    }

    [Fact]
    public void BuildHtml_Should_IncludeImpactedFunctionalityAndTestUseCases()
    {
        var builder = new ChurnReportBuilder(new ChurnSummarizer());
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 2, changes: Change("x", RegressionChangeKind.Commit))
            with { RegressionAreas = ["Alarming"], UseCases = ["UC-1"] };

        var html = builder.BuildHtml(Report(row));

        Assert.Contains("Impacted Functionality", html);
        Assert.Contains("Test Use Cases", html);
        Assert.Contains("Alarming", html);
        Assert.Contains("UC-1", html);
    }
}
