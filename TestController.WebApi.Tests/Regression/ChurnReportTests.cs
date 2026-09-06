using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;
using System.IO;
using ClosedXML.Excel;

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
    public void BuildCsv_Should_IncludeWorkItemLinksAndModifiedFiles()
    {
        var wi = new RegressionWorkItemRef(4105526, RegressionWorkItemKind.Story, "Safe DLL loading",
            "https://dev.azure.com/AVEVA-VSTS/_workitems/edit/4105526");
        var row = Row("ExtInterfaces", RegressionCategoryKind.Runtime, 1,
                changes: Change("Enable/disable Safe loading of DLL", RegressionChangeKind.PullRequest, wi))
            with { FilesModified = ["ExtInterfaces/ClassUtilities/SafeDllLoadHelper.cs"] };

        var csv = new ChurnReportBuilder(new ChurnSummarizer()).BuildCsv(Report(row));

        Assert.Contains("User Story 4105526", csv);
        Assert.Contains("https://dev.azure.com/AVEVA-VSTS/_workitems/edit/4105526", csv);
        Assert.Contains("SafeDllLoadHelper.cs", csv);
    }

    [Fact]
    public void BuildHtml_Should_LinkWorkItems()
    {
        var wi = new RegressionWorkItemRef(555, RegressionWorkItemKind.Bug, "crash", "https://ado/wi/555");
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 1,
            changes: Change("fix crash", RegressionChangeKind.PullRequest, wi));

        var html = new ChurnReportBuilder(new ChurnSummarizer()).BuildHtml(Report(row));

        Assert.Contains("href=\"https://ado/wi/555\"", html);
        Assert.Contains("Bug 555", html);
    }

    [Fact]
    public void BuildHtml_Should_MirrorExcelColumns_WithClubbedActivityAndSourceOnlyFiles()
    {
        var wi = new RegressionWorkItemRef(555, RegressionWorkItemKind.Bug, "crash", "https://ado/wi/555");
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 23,
                changes: Change("Merged PR: fix crash", RegressionChangeKind.PullRequest, wi))
            with { FilesModified = ["src/Engine.cpp", "README.md"] };

        var html = new ChurnReportBuilder(new ChurnSummarizer()).BuildHtml(Report(row));

        Assert.Contains(">Activity<", html);
        Assert.Contains(">Work Items<", html);
        Assert.Contains(">Change Links<", html);
        Assert.Contains("Files changed: 23", html);
        Assert.Contains("Engine.cpp", html);
        Assert.DoesNotContain("README.md", html); // source-only files (.h/.cpp/.cs), matching the Excel
        Assert.Contains("href=\"https://ado/wi/555\"", html);
    }

    [Fact]
    public void BuildCsv_Should_IncludeChangeAzureLinks()
    {
        var change = new RegressionChangeRef("PR-960370", "Merged PR", DateTimeOffset.UtcNow, [], [],
            RegressionChangeKind.PullRequest, "https://dev.azure.com/AVEVA-VSTS/System%20Platform/_git/repo/pullrequest/960370");
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 1, changes: change);

        var csv = new ChurnReportBuilder(new ChurnSummarizer()).BuildCsv(Report(row));

        Assert.Contains("pullrequest/960370", csv);
    }

    [Fact]
    public void BuildXlsx_Should_ProduceWorkbookWithHeaderAndHyperlink()
    {
        var wi = new RegressionWorkItemRef(4105526, RegressionWorkItemKind.Story, "Safe DLL",
            "https://dev.azure.com/AVEVA-VSTS/_workitems/edit/4105526");
        var row = Row("ExtInterfaces", RegressionCategoryKind.Runtime, 1,
                changes: Change("Safe DLL load", RegressionChangeKind.PullRequest, wi))
            with { FilesModified = ["ExtInterfaces/SafeDllLoadHelper.cs"], Repository = "AppServer.ExtInterfaces" };

        var bytes = new ChurnXlsxBuilder().BuildXlsx(Report(row));

        Assert.NotEmpty(bytes);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        Assert.Equal("Component", ws.Cell(4, 2).GetString());
        Assert.Contains("ExtInterfaces", ws.Cell(5, 2).GetString());
        Assert.True(ws.Cell(5, 10).HasHyperlink); // work-item link (Work Items column)
    }

    [Fact]
    public void BuildXlsx_Should_ClubActivityCountsAndFilterSourceFiles()
    {
        var wi = new RegressionWorkItemRef(5002965, RegressionWorkItemKind.Story, "story", "https://ado/wi/5002965");
        var row = Row("AAMxCore", RegressionCategoryKind.Runtime, 23,
                changes: Change("Merged PR 1: fix", RegressionChangeKind.PullRequest, wi))
            with { FilesModified = ["src/Engine.cpp", "src/Engine.h", "src/App.cs", "README.md", "docs/notes.txt"] };

        var bytes = new ChurnXlsxBuilder().BuildXlsx(Report(row));

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        var activity = ws.Cell(5, 6).GetString();
        Assert.Contains("Files changed: 23", activity);
        Assert.Contains("PR's: 1", activity);
        Assert.Contains("WI: 1", activity);
        var files = ws.Cell(5, 11).GetString();
        Assert.Contains("Engine.cpp", files);
        Assert.Contains("Engine.h", files);
        Assert.Contains("App.cs", files);
        Assert.DoesNotContain("README.md", files);
        Assert.DoesNotContain("notes.txt", files);
    }

    [Fact]
    public void BuildXlsx_Should_AddImpactedTestCasesSheet_When_MatchesProvided()
    {
        var row = Row("Cybersecurity", RegressionCategoryKind.Runtime, 1,
            changes: Change("PR", RegressionChangeKind.PullRequest));
        var matches = new[]
        {
            new ImpactedTestCaseMatch("Cybersecurity", 4536616, "BootStrap.TC24", "steps",
                "https://dev.azure.com/AVEVA-VSTS/_workitems/edit/4536616", 0, "partial", 84, "exercises bootstrap sync"),
        };

        var bytes = new ChurnXlsxBuilder().BuildXlsx(Report(row), matches);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.True(wb.TryGetWorksheet("Impacted Test Cases", out var ws));
        Assert.Equal("Impacted Area", ws.Cell(4, 1).GetString());
        Assert.Equal("Match Reason", ws.Cell(4, 7).GetString());
        Assert.Contains("BootStrap.TC24", ws.Cell(5, 3).GetString());
        Assert.Equal("does not exist", ws.Cell(5, 4).GetString());
        Assert.True(ws.Cell(5, 2).HasHyperlink); // TC id deep link
    }

    [Fact]
    public void BuildXlsx_Should_OmitImpactedTestCasesSheet_When_NoMatches()
    {
        var row = Row("Alpha", RegressionCategoryKind.Runtime, 1, changes: Change("x", RegressionChangeKind.Commit));

        var bytes = new ChurnXlsxBuilder().BuildXlsx(Report(row));

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.False(wb.TryGetWorksheet("Impacted Test Cases", out _));
        Assert.Equal(3, wb.Worksheets.Count); // Code Churn + Work Items + Changes
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

        Assert.Contains("Impacted Functionality", lines[0]);
        Assert.Contains("Test Use Cases", lines[0]);
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
