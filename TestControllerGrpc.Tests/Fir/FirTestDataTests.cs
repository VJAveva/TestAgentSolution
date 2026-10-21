using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Phase 3: the log's "Row No: N" joined to row N of the sheet named after the test. Exercises the real
/// ClosedXmlTestDataSheetReader against the sanitized fixture workbook.
/// </summary>
[Collection("TokenState")]
public class FirTestDataTests : IDisposable
{
    private readonly TempDirectory _results = FirFixtures.NewResultsFolder();

    public FirTestDataTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        _results.Dispose();
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private FirReport Correlated(string fixture)
        => new FirAnalyzer(FirFixtures.Packs(), FirFixtures.SheetReader())
            .Analyze(FirFixtures.Load(fixture, _results.Path))!;

    private static FirReport LogOnly(string fixture)
        => new FirAnalyzer(FirFixtures.Packs()).Analyze(FirFixtures.Load(fixture))!;

    private static FirReportRow Row(FirReport report, int row)
        => report.Rows.Single(r => r.Row == row);

    [Fact]
    public void Analyze_Should_NameTheRootStepFromTheSheet_When_TheWorkbookIsReadable()
    {
        var report = Correlated("S1_13_TPTest3.log");

        var step = report.Root!.DataStep!;
        Assert.Equal(23, step.Row);
        Assert.Equal("Appserver", step.Component);
        Assert.Equal("redeploy", step.Keyword);
        Assert.StartsWith("DDESL_002_", step.Object);
    }

    [Fact]
    public void Analyze_Should_ReadTheWholeStep_When_TheRootDeploysTheGalaxy()
    {
        var report = Correlated("S1_04_SmokeTestDeploy.log");

        Assert.Equal("Appserver | deploy | SmokeSet1 | true | true | false", report.Root!.DataStep!.Display);
    }

    [Fact]
    public void Analyze_Should_LinkCascadesHighConfidence_When_TheyTouchTheRootsObject()
    {
        var report = Correlated("S1_13_TPTest3.log");

        foreach (var row in new[] { 29, 30, 31, 32 })
        {
            Assert.Equal(FirLink.High, Row(report, row).Link);
            Assert.StartsWith("DDESL_002_", Row(report, row).DataStep!.Object);
        }
    }

    [Fact]
    public void Analyze_Should_LinkCascadesHighConfidence_When_TheyRunOnTheRootsHostPlatform()
    {
        // Rows 24/25 deploy ViewEngine_002/_003 - different objects, but hosted by the platforms that
        // failed at the root, which only the product output reveals.
        var report = Correlated("S1_04_SmokeTestDeploy.log");

        Assert.Equal(FirLink.High, Row(report, 24).Link);
        Assert.Equal(FirLink.High, Row(report, 25).Link);
    }

    [Fact]
    public void Analyze_Should_ShowAPassedRow_When_ItProvesTheOperationWorksElsewhere()
    {
        var report = Correlated("S1_04_SmokeTestDeploy.log");

        var row = Row(report, 23);
        Assert.Equal(FirRowTag.Passed, row.Tag);
        Assert.Equal("ViewEngine_001", row.DataStep!.Object);
    }

    [Fact]
    public void Analyze_Should_ShowAPassedRow_When_TheRootsObjectStillAnsweredAfterwards()
    {
        var report = Correlated("S1_13_TPTest3.log");

        Assert.Equal(FirRowTag.Passed, Row(report, 28).Tag);
        Assert.Equal(FirRowTag.Passed, Row(report, 24).Tag);
    }

    [Fact]
    public void Analyze_Should_KeepRecoveredRows_When_TheSheetIsAlsoAvailable()
    {
        var report = Correlated("S1_04_SmokeTestDeploy.log");

        Assert.Equal([33, 34], report.Rows.Where(r => r.Tag == FirRowTag.Recovered).Select(r => r.Row));
    }

    [Fact]
    public void Analyze_Should_StateTheGap_When_TheRootsObjectPassedBetweenRootAndCascade()
    {
        var report = Correlated("S1_13_TPTest3.log");

        Assert.NotNull(report.OpenPoint);
        Assert.Contains("Row 28 passed on DDESL_002", report.OpenPoint);
        Assert.Contains("does not show what changed between rows 28 and 29", report.OpenPoint);
    }

    [Fact]
    public void Analyze_Should_NotInventAnOpenPoint_When_NoPassedRowSitsInTheGap()
    {
        var report = Correlated("S1_04_SmokeTestDeploy.log");

        Assert.Null(report.OpenPoint);
    }

    [Fact]
    public void Analyze_Should_NameTheSheetStepInTheSummary_When_TheWorkbookWasRead()
    {
        var report = Correlated("S1_13_TPTest3.log");

        Assert.Contains("Cause: row 23 `Appserver | redeploy | DDESL_002\u2026`", report.Summary);
        // The workbook named is the one the LOG referenced, not wherever the reader found it.
        Assert.Contains("test data (WASSet1.xlsx > S1_13_TPTest3, copy from run)", report.Summary);
    }

    [Fact]
    public void Analyze_Should_DegradeToLogOnly_When_NoWorkbookIsAvailable()
    {
        var report = LogOnly("S1_13_TPTest3.log");

        // Same root and cascade, just without the sheet step.
        Assert.Equal(23, report.Root!.Row);
        Assert.Null(report.Root.DataStep);
        Assert.Equal([29, 30, 31, 32], report.Rows.Where(r => r.Tag == FirRowTag.Cascade).Select(r => r.Row));
        Assert.Contains("test data (WASSet1.xlsx > S1_13_TPTest3, not found)", report.Summary);
    }

    [Fact]
    public void Read_Should_ReturnNull_When_TheWorkbookIsMissing()
    {
        var reader = new TestControllerGrpc.Reporting.Fir.ClosedXmlTestDataSheetReader();

        Assert.Null(reader.Read(Path.Combine(Path.GetTempPath(), "no-such-book.xlsx"), "Sheet1", FirTestDataLayout.Default));
    }

    [Fact]
    public void Read_Should_ReturnNull_When_TheSheetIsNotInTheWorkbook()
    {
        var reader = new TestControllerGrpc.Reporting.Fir.ClosedXmlTestDataSheetReader();
        var book = Path.Combine(FirFixtures.FixtureDirectory, "WASSet1.fixture.xlsx");

        Assert.Null(reader.Read(book, "NoSuchSheet", FirTestDataLayout.Default));
    }

    [Fact]
    public void Read_Should_SkipRegionMarkersAndUnwrapKeywords_When_TheLayoutSaysSo()
    {
        var layout = FirFixtures.Packs().Single(p => p.Id == "WASSmokeTest").TestData!;
        var reader = new TestControllerGrpc.Reporting.Fir.ClosedXmlTestDataSheetReader();
        var book = Path.Combine(FirFixtures.FixtureDirectory, "WASSet1.fixture.xlsx");

        var steps = reader.Read(book, "S1_13_TPTest3", layout)!;

        Assert.DoesNotContain(steps.Values, s => s.Component.Contains("region#", StringComparison.OrdinalIgnoreCase));
        // The sheet writes object-viewer keywords as "<modifyovvalue>".
        Assert.Equal("modifyovvalue", steps[29].Keyword);
        Assert.Equal("objectviewer", steps[29].Component);
    }

    [Fact]
    public void Read_Should_SplitParameters_When_TheyShareOneCell()
    {
        var layout = FirFixtures.Packs().Single(p => p.Id == "WASSmokeTest").TestData!;
        var reader = new TestControllerGrpc.Reporting.Fir.ClosedXmlTestDataSheetReader();
        var book = Path.Combine(FirFixtures.FixtureDirectory, "WASSet1.fixture.xlsx");

        var steps = reader.Read(book, "S1_04_SmokeTestDeploy", layout)!;

        Assert.Equal(["SmokeSet1", "true", "true", "false"], steps[6].Params);
    }
}
