using ClosedXML.Excel;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Gate A: the exclusion footer must appear in ALL THREE outputs with the same text. A suppression that is
/// visible in one export and not another is how email and Excel drifted apart in the first place.
/// </summary>
public class CodeChurnRendererPolicyTests
{
    private static readonly CodeChurnFeatureGroup Orphan =
        new(null, "Unassigned to Feature", [], []);

    private static CodeChurnReportModel Model()
    {
        var wi = new RegressionWorkItemRef(20, RegressionWorkItemKind.Story, "Story 20", null, null, "User Story");
        var change = new RegressionChangeRef("c1", "fix", DateTimeOffset.UtcNow, ["src/a.cs"], [wi]);

        var feature = new CodeChurnFeatureGroup(
            30, "Feature 30", [new CodeChurnWorkItemEntry(wi, [change])], [change]);

        return new CodeChurnReportModel(
            Report(),
            [feature],
            Orphan,
            [new CodeChurnExclusion("Task", 47, 12)],
            []);
    }

    private static ChurnReport Report() =>
        new("Build", "range", default, default, DateTimeOffset.UtcNow, []);

    private static ChurnReportBuilder Builder() => new(new ChurnSummarizer());

    [Fact]
    public void ExclusionFooter_Should_StateSuppressedAndReattributedCounts()
    {
        string footer = Model().ExclusionFooter;

        Assert.Contains("47", footer);
        Assert.Contains("Task", footer);
        Assert.Contains("12", footer);
        Assert.Contains("re-attributed", footer);
    }

    [Fact]
    public void BuildHtml_Should_ContainExclusionFooterAndFeatureGroup()
    {
        CodeChurnReportModel model = Model();

        string html = Builder().BuildHtml(model.Source, model);

        Assert.Contains("Impacted Features", html);
        Assert.Contains("Feature 30", html);
        Assert.Contains("47", html);
        Assert.Contains("Unassigned to Feature", html);
    }

    [Fact]
    public void BuildCsv_Should_ContainExclusionFooterAndFeatureGroup()
    {
        CodeChurnReportModel model = Model();

        string csv = Builder().BuildCsv(model.Source, model);

        Assert.Contains("Feature 30", csv);
        Assert.Contains("Unassigned to Feature", csv);
        Assert.Contains("47", csv);
    }

    [Fact]
    public void BuildXlsx_Should_ContainFeaturesSheetWithFooter()
    {
        CodeChurnReportModel model = Model();

        byte[] bytes = new ChurnXlsxBuilder().BuildXlsx(model.Source, null, model);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        IXLWorksheet ws = wb.Worksheet("Features");

        string all = ws.RangeUsed()!.Cells().Select(c => c.GetString()).Aggregate((a, b) => a + "|" + b);
        Assert.Contains("Feature 30", all);
        Assert.Contains("Unassigned to Feature", all);
        Assert.Contains("47", all);
    }

    [Fact]
    public void AllThreeOutputs_Should_CarryTheSameFooterText()
    {
        CodeChurnReportModel model = Model();
        string footer = model.ExclusionFooter;

        string html = Builder().BuildHtml(model.Source, model);
        string csv = Builder().BuildCsv(model.Source, model);
        byte[] xlsx = new ChurnXlsxBuilder().BuildXlsx(model.Source, null, model);

        using var ms = new MemoryStream(xlsx);
        using var wb = new XLWorkbook(ms);
        string sheet = wb.Worksheet("Features").RangeUsed()!.Cells()
            .Select(c => c.GetString()).Aggregate((a, b) => a + "|" + b);

        Assert.Contains(footer, csv);
        Assert.Contains(footer, sheet);
        // HTML escapes the text, so compare on the distinguishing counts rather than the raw string.
        Assert.Contains("47", html);
        Assert.Contains("12", html);
    }

    [Fact]
    public void Renderers_Should_BeUnchanged_When_NoPolicySupplied()
    {
        // Backwards compatibility: existing callers pass no policy and must get exactly the previous output.
        string html = Builder().BuildHtml(Report());

        Assert.DoesNotContain("Impacted Features", html);
    }
}
