using ClosedXML.Excel;
using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Phase 4a: finding the workbook on the controller, and refusing one that no longer matches the run.
/// </summary>
[Collection("TokenState")]
public class FirWorkbookResolutionTests : IDisposable
{
    private readonly List<TempDirectory> _temp = [];

    public FirWorkbookResolutionTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        foreach (var dir in _temp) dir.Dispose();
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private TempDirectory Track(TempDirectory dir)
    {
        _temp.Add(dir);
        return dir;
    }

    private FirReport Analyze(string? resultsFolder, FirOptions? options = null)
        => new FirAnalyzer(FirFixtures.Packs(), FirFixtures.SheetReader(), options)
            .Analyze(FirFixtures.Load("S1_13_TPTest3.log", resultsFolder))!;

    [Fact]
    public void Analyze_Should_PreferTheCopyFromTheRun_When_ItCameBackWithTheResults()
    {
        var results = Track(FirFixtures.NewResultsFolder());

        var report = Analyze(results.Path);

        Assert.Equal(FirDataSource.CopyFromRun, report.DataSource);
        Assert.Equal(23, report.Root!.Row);
        Assert.NotNull(report.Root.DataStep);
        Assert.Contains("copy from run", report.Summary);
    }

    [Fact]
    public void Analyze_Should_FallBackToAControllerCopy_When_TheRunBroughtNone()
    {
        var results = Track(FirFixtures.NewResultsFolder(workbookName: ""));
        var root = Track(FirFixtures.NewResultsFolder());

        var report = Analyze(results.Path, new FirOptions { TestDataRoots = [root.Path] });

        Assert.Equal(FirDataSource.ControllerCopy, report.DataSource);
        Assert.Equal("redeploy", report.Root!.DataStep!.Keyword);
        Assert.Contains("controller copy, matched to log", report.Summary);
    }

    [Fact]
    public void Analyze_Should_ReportLogOnly_When_NoCopyOfTheWorkbookExists()
    {
        var results = Track(FirFixtures.NewResultsFolder(workbookName: ""));

        var report = Analyze(results.Path);

        Assert.Equal(FirDataSource.None, report.DataSource);
        Assert.Null(report.Root!.DataStep);
        // Still a full log-only report.
        Assert.Equal(23, report.Root.Row);
        Assert.Contains("test data (WASSet1.xlsx > S1_13_TPTest3, not found)", report.Summary);
    }

    [Fact]
    public void Resolve_Should_IgnoreTheLoggedPath_When_ItIsNotUnderTheResultsFolderOrARoot()
    {
        // The logged path belongs to the agent. Even if some file sits there, it is not ours to trust,
        // and nothing may be fetched across the network.
        var elsewhere = Track(FirFixtures.NewResultsFolder());
        var logged = Path.Combine(elsewhere.Path, "TestData", "WASSet1.xlsx");
        Assert.True(File.Exists(logged));

        var match = FirWorkbookResolver.Resolve(logged, resultsFolder: null, options: FirOptions.Default);

        Assert.Null(match);
    }

    [Fact]
    public void Analyze_Should_RejectTheWorkbook_When_ARowWasInsertedSinceTheRun()
    {
        var results = Track(FirFixtures.NewResultsFolder());
        InsertRowAbove(Path.Combine(results.Path, "TestData", "WASSet1.xlsx"), "S1_13_TPTest3", 2);

        var report = Analyze(results.Path);

        Assert.Equal(FirDataSource.None, report.DataSource);
        Assert.NotNull(report.DataWarning);
        Assert.Contains("changed since this run", report.Summary);
        // Attribution is dropped, but the log-only report survives intact.
        Assert.Equal(23, report.Root!.Row);
        Assert.Null(report.Root.DataStep);
        Assert.Equal([29, 30, 31, 32], report.Rows.Where(r => r.Tag == FirRowTag.Cascade).Select(r => r.Row));
    }

    [Fact]
    public void Analyze_Should_RejectTheWorkbook_When_AKeywordNoLongerMatchesTheLog()
    {
        var results = Track(FirFixtures.NewResultsFolder());
        var book = Path.Combine(results.Path, "TestData", "WASSet1.xlsx");

        using (var wb = new XLWorkbook(book))
        {
            // Row 23 ran DeployOperation in the log; claim it was an undeploy instead.
            wb.Worksheet("S1_13_TPTest3").Cell(23, 2).Value = "undeploy";
            wb.Save();
        }

        var report = Analyze(results.Path);

        Assert.Equal(FirDataSource.None, report.DataSource);
        Assert.Contains("row 23", report.DataWarning);
    }

    [Fact]
    public void Analyze_Should_RejectTheWorkbook_When_ARowUsedByTheLogIsNoLongerAStep()
    {
        // Isolates the row rule: clearing a row the log used shifts nothing, so no keyword can
        // disagree. Without the row check this workbook would be accepted.
        var results = Track(FirFixtures.NewResultsFolder());
        var book = Path.Combine(results.Path, "TestData", "WASSet1.xlsx");

        using (var wb = new XLWorkbook(book))
        {
            wb.Worksheet("S1_13_TPTest3").Row(2).Clear();
            wb.Save();
        }

        var report = Analyze(results.Path);

        Assert.Equal(FirDataSource.None, report.DataSource);
        Assert.Contains("no step at row 2", report.DataWarning);
    }

    private static void InsertRowAbove(string workbook, string sheet, int row)
    {
        using var wb = new XLWorkbook(workbook);
        wb.Worksheet(sheet).Row(row).InsertRowsAbove(1);
        wb.Save();
    }
}
