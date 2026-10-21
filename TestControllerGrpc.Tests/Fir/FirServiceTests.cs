using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Phase 4: on-demand compute and the &lt;Test&gt;.fir.json cache. The TRX here is synthesized from the
/// fixture log, which also proves the TRX alone yields the same root as the log did.
/// </summary>
[Collection("TokenState")]
public class FirServiceTests : IDisposable
{
    private const string Build = "OAK_main_20261005.3";
    private const string Suite = "Set1";
    private const string Test = "S1_13_TPTest3";

    private readonly TempDirectory _root = new("FirRoot");

    public FirServiceTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        _root.Dispose();
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private string SuiteFolder
    {
        get
        {
            var folder = Path.Combine(_root.Path, Build, Suite);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private string CachePath => Path.Combine(_root.Path, Build, Suite, Test + ".fir.json");

    private FirService Service()
    {
        var packs = FirFixtures.Packs();
        return new FirService(
            new TrxResultsParser(),
            new FirAnalyzer(packs, FirFixtures.SheetReader()),
            new BuildResultsConfig { ResultsRootPath = _root.Path },
            new FirRulePackSet(packs, []),
            new NoopLogger());
    }

    /// <summary>Lays out a build the way the agent leaves one, optionally with the workbook.</summary>
    private string Arrange(string fixture = Test + ".log", bool withWorkbook = true, string? outcome = null)
    {
        var folder = SuiteFolder;
        var trx = FirFixtures.WriteTrx(folder, fixture, outcome);

        if (withWorkbook) CopyWorkbook();

        return trx;
    }

    /// <summary>The copy the run brought back, under &lt;results&gt;\TestData.</summary>
    private void CopyWorkbook()
    {
        var testData = Path.Combine(SuiteFolder, "TestData");
        Directory.CreateDirectory(testData);
        File.Copy(
            Path.Combine(FirFixtures.FixtureDirectory, "WASSet1.fixture.xlsx"),
            Path.Combine(testData, "WASSet1.xlsx"), overwrite: true);
    }

    [Fact]
    public void GetOrCompute_Should_FindTheSameRoot_When_OnlyTheTrxIsAvailable()
    {
        Arrange();

        var report = Service().GetOrCompute(Build, Test)!;

        Assert.Equal(23, report.Root!.Row);
        Assert.Equal(FirRowTag.Masked, report.Root.Tag);
        Assert.Equal([29, 30, 31, 32], report.Rows.Where(r => r.Tag == FirRowTag.Cascade).Select(r => r.Row));
        Assert.Equal("redeploy", report.Root.DataStep!.Keyword);
        Assert.Equal(FirDataSource.CopyFromRun, report.DataSource);
    }

    [Fact]
    public void GetOrCompute_Should_CacheTheReport_BesideTheTrx()
    {
        Arrange();

        var report = Service().GetOrCompute(Build, Test)!;

        Assert.True(File.Exists(CachePath));
        Assert.False(string.IsNullOrWhiteSpace(report.CacheKey));
        Assert.Contains(report.CacheKey, File.ReadAllText(CachePath));
    }

    [Fact]
    public void GetOrCompute_Should_ServeTheCache_When_NoInputChanged()
    {
        Arrange();
        var service = Service();
        service.GetOrCompute(Build, Test);

        // Mark the cached file; a recompute would overwrite the marker.
        File.WriteAllText(CachePath, File.ReadAllText(CachePath).Replace("Root cause:", "CACHED MARKER:"));

        Assert.StartsWith("CACHED MARKER:", service.GetOrCompute(Build, Test)!.Verdict);
    }

    [Fact]
    public void GetOrCompute_Should_Recompute_When_TheTrxChanged()
    {
        var trx = Arrange();
        var service = Service();
        service.GetOrCompute(Build, Test);
        File.WriteAllText(CachePath, File.ReadAllText(CachePath).Replace("Root cause:", "CACHED MARKER:"));

        File.SetLastWriteTimeUtc(trx, DateTime.UtcNow.AddMinutes(1));

        Assert.DoesNotContain("CACHED MARKER", service.GetOrCompute(Build, Test)!.Verdict);
    }

    [Fact]
    public void GetOrCompute_Should_Recompute_When_ARulePackVersionChanged()
    {
        Arrange();
        Service().GetOrCompute(Build, Test);
        File.WriteAllText(CachePath, File.ReadAllText(CachePath).Replace("Root cause:", "CACHED MARKER:"));

        var packs = FirFixtures.Packs();
        var bumped = new FirService(
            new TrxResultsParser(),
            new FirAnalyzer(packs, FirFixtures.SheetReader()),
            new BuildResultsConfig { ResultsRootPath = _root.Path },
            new FirRulePackSet(packs, []) with { Packs = [.. packs.Select(p => p with { Version = p.Version + 1 })] },
            new NoopLogger());

        Assert.DoesNotContain("CACHED MARKER", bumped.GetOrCompute(Build, Test)!.Verdict);
    }

    [Fact]
    public void GetOrCompute_Should_Recompute_When_RefreshIsAsked()
    {
        Arrange();
        var service = Service();
        service.GetOrCompute(Build, Test);
        File.WriteAllText(CachePath, File.ReadAllText(CachePath).Replace("Root cause:", "CACHED MARKER:"));

        Assert.DoesNotContain("CACHED MARKER", service.GetOrCompute(Build, Test, refresh: true)!.Verdict);
    }

    /// <summary>
    /// The workbook arrives after the report was cached - which is exactly what happens the first time a
    /// run copies one back. No file the cached report names has changed, so the key must be derived from a
    /// fresh resolution to notice.
    /// </summary>
    [Fact]
    public void GetOrCompute_Should_Recompute_When_TheWorkbookBecomesResolvableLater()
    {
        Arrange(withWorkbook: false);
        var service = Service();
        Assert.Equal(FirDataSource.None, service.GetOrCompute(Build, Test)!.DataSource);
        File.WriteAllText(CachePath, File.ReadAllText(CachePath).Replace("Root cause:", "CACHED MARKER:"));

        CopyWorkbook();

        var report = service.GetOrCompute(Build, Test)!;

        Assert.Equal(FirDataSource.CopyFromRun, report.DataSource);
        Assert.DoesNotContain("CACHED MARKER", report.Verdict);
    }

    [Fact]
    public void GetOrCompute_Should_ReturnNull_When_TheTestPassed()
    {
        Arrange(outcome: "Passed");

        Assert.Null(Service().GetOrCompute(Build, Test));
        Assert.False(File.Exists(CachePath));
    }

    [Theory]
    [InlineData("no-such-build", Test)]
    [InlineData(Build, "NoSuchTest")]
    [InlineData("", "")]
    public void GetOrCompute_Should_ReturnNull_When_TheBuildOrTestIsUnknown(string build, string test)
    {
        Arrange();

        Assert.Null(Service().GetOrCompute(build, test));
    }

    [Fact]
    public void GetOrCompute_Should_StillReport_When_TheWorkbookIsAbsent()
    {
        Arrange(withWorkbook: false);

        var report = Service().GetOrCompute(Build, Test)!;

        Assert.Equal(23, report.Root!.Row);
        Assert.Null(report.Root.DataStep);
        Assert.Equal(FirDataSource.None, report.DataSource);
    }

    [Fact]
    public void Backfill_Should_WriteAReportForEveryFailedTest()
    {
        Arrange();
        FirFixtures.WriteTrx(SuiteFolder, "S1_04_SmokeTestDeploy.log");

        var written = Service().Backfill(Build);

        Assert.Equal(2, written);
        Assert.True(File.Exists(CachePath));
        Assert.True(File.Exists(Path.Combine(_root.Path, Build, Suite, "S1_04_SmokeTestDeploy.fir.json")));
    }

    private sealed class NoopLogger : IAppLogger
    {
        public event Action<AppLogEntry>? EntryAdded;

        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }

        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message,
            string? correlationId, long elapsedMs = 0, Exception? ex = null) { }

        public void LogStructured(Microsoft.Extensions.Logging.LogLevel level, string category, string message,
            string? agent = null, string? runId = null, string? pipeline = null,
            string? action = null, long elapsedMs = 0, Exception? ex = null) { }

        public void Info(string category, string message) { }

        public void Warn(string category, string message) { }

        public void Error(string category, string message, Exception? ex = null) { }

        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
