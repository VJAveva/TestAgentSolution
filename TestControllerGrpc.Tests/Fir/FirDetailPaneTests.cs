using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// The Detail pane's own logic: what the view model does with a report. The XAML is covered by the Ui
/// guards; this covers the part that can be wrong without any resource failing to resolve.
/// </summary>
[Collection("TokenState")]
public class FirDetailPaneTests : IDisposable
{
    private const string Build = "OAK_main_20261005.3";
    private const string Suite = "Set1";

    private readonly TempDirectory _root = new("FirPane");

    public FirDetailPaneTests()
    {
        SecurityRedactor.ClearSecretValues();

        var folder = Path.Combine(_root.Path, Build, Suite);
        Directory.CreateDirectory(folder);
        FirFixtures.WriteTrx(folder, "S1_13_TPTest3.log");

        var testData = Path.Combine(folder, "TestData");
        Directory.CreateDirectory(testData);
        File.Copy(
            Path.Combine(FirFixtures.FixtureDirectory, "WASSet1.fixture.xlsx"),
            Path.Combine(testData, "WASSet1.xlsx"));
    }

    public void Dispose()
    {
        _root.Dispose();
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private BuildResultsViewModel ViewModel()
    {
        var config = new BuildResultsConfig { ResultsRootPath = _root.Path };
        var parser = new TrxResultsParser();
        var packs = FirFixtures.Packs();

        return new BuildResultsViewModel(
            parser,
            new BuildResultsAggregator(config),
            config,
            new BuildReportHtmlGenerator(config),
            firService: new FirService(
                parser,
                new FirAnalyzer(packs, FirFixtures.SheetReader()),
                config,
                new FirRulePackSet(packs, []),
                new SilentLogger()));
    }

    private static TestResult Result(string name, string outcome = "Failed")
        => new() { TestName = name, Outcome = outcome, UseCaseName = Suite };

    [Fact]
    public async Task PopulateFir_Should_FillThePane_When_TheTestFailed()
    {
        var vm = ViewModel();

        await vm.PopulateFir(Build, Result("S1_13_TPTest3"));

        Assert.True(vm.HasFir);
        Assert.Contains("row 23", vm.FirVerdict);
        Assert.Contains(vm.FirRows, r => r.Tag == "MASKED" && r.Row == "23");
        Assert.Contains("Validation mismatch: 3", vm.FirCategories);
        // The raw TRX sections give way to the report.
        Assert.False(vm.ShowRawOutput);
    }

    [Fact]
    public async Task PopulateFir_Should_WarnAboutCredentials_When_TheLogPrintedThem()
    {
        var vm = ViewModel();

        await vm.PopulateFir(Build, Result("S1_13_TPTest3"));

        Assert.Contains("4 line(s)", vm.FirSecretWarning);
    }

    [Fact]
    public async Task PopulateFir_Should_LeaveThePaneEmpty_When_TheTestPassed()
    {
        var vm = ViewModel();

        await vm.PopulateFir(Build, Result("S1_13_TPTest3", outcome: "Passed"));

        Assert.False(vm.HasFir);
        Assert.Empty(vm.FirRows);
        // With no report there is nothing to hide the raw output behind.
        Assert.True(vm.ShowRawOutput);
    }

    [Fact]
    public async Task PopulateFir_Should_IgnoreAStaleReport_When_AnotherTestWasSelectedFirst()
    {
        var vm = ViewModel();

        // The second selection is a PASSING test, which returns immediately. Without the generation
        // guard the first test's report lands afterwards and the pane shows a report for the wrong test.
        var inFlight = vm.PopulateFir(Build, Result("S1_13_TPTest3"));
        await vm.PopulateFir(Build, Result("S1_21_GalaxyStatus", outcome: "Passed"));
        await inFlight;

        Assert.False(vm.HasFir);
        Assert.Empty(vm.FirRows);
    }

    private sealed class SilentLogger : IAppLogger
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
