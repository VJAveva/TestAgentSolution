using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// Phase 2 exit criteria, asserted against the real sanitized logs from OAK_main_20261005.3 / Set1.
/// Shares TokenState because the secret count reads SecurityRedactor's static registry.
/// </summary>
[Collection("TokenState")]
public class FirRulePackTests : IDisposable
{
    public FirRulePackTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    private static FirReport Analyze(string fixture)
        => new FirAnalyzer(FirFixtures.Packs()).Analyze(FirFixtures.Load(fixture))!;

    private static IReadOnlyList<int> RowsTagged(FirReport report, FirRowTag tag)
        => [.. report.Rows.Where(r => r.Tag == tag).Select(r => r.Row)];

    [Fact]
    public void Analyze_Should_StackBothPacks_When_TraceMatchesFrameworkAndProduct()
    {
        var report = Analyze("S1_13_TPTest3.log");

        Assert.Equal(["generic", "WASSmokeTest@1", "AppServer@1"], report.RulePacks);
    }

    [Theory]
    [InlineData("S1_04_SmokeTestDeploy.log", 32, 1, 14)]
    [InlineData("S1_13_TPTest3.log", 5, 0, 1)]
    public void Analyze_Should_ReadTheReasonCounts_When_FrameworkPackSuppliesThePattern(
        string fixture, int validation, int errors, int warnings)
    {
        var report = Analyze(fixture);

        Assert.Equal(new FirReasonCounts(validation, errors, warnings), report.Reason);
        // The framework's own count and the steps we parsed must agree, or one of them is being misread.
        Assert.Equal(validation, report.FailedSteps);
    }

    [Fact]
    public void Analyze_Should_PickTheMaskedRowAsRoot_When_TheKeywordLoggedPassedButNothingDeployed()
    {
        var report = Analyze("S1_13_TPTest3.log");

        // The first RED line is row 29; the cause is row 23, which logged Passed.
        Assert.Equal(23, report.Root!.Row);
        Assert.Equal(FirRowTag.Masked, report.Root.Tag);
        Assert.Equal("DeployOperation", report.Root.Keyword);
        Assert.Contains("completed 0 of 1", report.Root.Evidence);
        Assert.Contains("Unable to communicate with the target engine.", report.Root.Evidence);
    }

    [Fact]
    public void Analyze_Should_TagEveryLaterFailingRowAsCascade_When_TheyFollowTheRoot()
    {
        var report = Analyze("S1_13_TPTest3.log");

        Assert.Equal([29, 30, 31, 32], RowsTagged(report, FirRowTag.Cascade));
    }

    [Fact]
    public void Analyze_Should_PickTheFirstFailingRowAsRoot_When_ItsOwnKeywordsFailed()
    {
        var report = Analyze("S1_04_SmokeTestDeploy.log");

        Assert.Equal(6, report.Root!.Row);
        Assert.Equal(FirRowTag.Root, report.Root.Tag);
        Assert.Equal("DeployOperation", report.Root.Keyword);
        Assert.Equal("AutoGalaxyConfiguration.cs:8622", report.Root.SourceFileLine);
    }

    [Fact]
    public void Analyze_Should_FindEveryFailingRow_When_TheRunHasCascadesAndAMaskedPass()
    {
        var report = Analyze("S1_04_SmokeTestDeploy.log");

        var failing = report.Rows
            .Where(r => r.Tag is FirRowTag.Root or FirRowTag.Cascade or FirRowTag.Masked)
            .Select(r => r.Row);

        Assert.Equal([6, 24, 25, 32], failing);
        Assert.Equal([32], RowsTagged(report, FirRowTag.Masked));
    }

    [Fact]
    public void Analyze_Should_TagRowsAsRecovered_When_TheSameOperationLaterSucceededOnTheSameObjects()
    {
        var report = Analyze("S1_04_SmokeTestDeploy.log");

        Assert.Equal([33, 34], RowsTagged(report, FirRowTag.Recovered));
    }

    [Fact]
    public void Analyze_Should_GroupRepeatedProductReasons_When_ManyObjectsShareOneCause()
    {
        var report = Analyze("S1_04_SmokeTestDeploy.log");

        Assert.Contains(new FirReason("Communication error: Platform communication error", 12), report.Root!.Reasons);
        Assert.Contains(new FirReason("Platform startup failed.", 2), report.Root.Reasons);
    }

    [Theory]
    [InlineData("S1_04_SmokeTestDeploy.log", new[] { 6 })]
    [InlineData("S1_13_TPTest3.log", new[] { 29, 30 })]
    public void Analyze_Should_FlagSlowRows_When_ARowRunsPastTheThreshold(string fixture, int[] expected)
    {
        var report = Analyze(fixture);

        Assert.Equal(expected, report.Rows.Where(r => r.IsSlow).Select(r => r.Row));
    }

    [Fact]
    public void Analyze_Should_CategorizeFailingSteps_When_TheProductPackDefinesCategories()
    {
        var report = Analyze("S1_13_TPTest3.log");

        Assert.Equal(3, report.Categories.Single(c => c.Name == "Validation mismatch").Count);
        Assert.Equal(2, report.Categories.Single(c => c.Name == "Runtime write failed").Count);
        Assert.Equal(1, report.Categories.Single(c => c.Name == "Masked pass").Count);
    }

    [Fact]
    public void Analyze_Should_CountProductErrorsAsOneCategory_When_EveryFailureIsADeploy()
    {
        var report = Analyze("S1_04_SmokeTestDeploy.log");

        Assert.Equal(32, report.Categories.Single(c => c.Name == "Product error").Count);
    }

    [Theory]
    [InlineData("S1_04_SmokeTestDeploy.log", 2)]
    [InlineData("S1_13_TPTest3.log", 4)]
    public void Analyze_Should_CountCredentialLines_When_TheLogPrintedThem(string fixture, int expected)
    {
        // The fixtures are sanitized, so the VALUES are gone. The pack still finds the leak because it
        // matches the GetUserToken line shape - which is the only thing that survives redaction.
        var report = Analyze(fixture);

        Assert.Equal(expected, report.SecretsFound);
    }

    [Theory]
    [InlineData("S1_04_SmokeTestDeploy.log")]
    [InlineData("S1_13_TPTest3.log")]
    public void Analyze_Should_HideHarnessNoise_When_VstestReportsDeploymentIssues(string fixture)
    {
        var report = Analyze(fixture);

        // Measured from the fixtures; the requirement's "64" counts one line that is not noise.
        Assert.Equal(63, report.NoiseLines);
    }

    [Theory]
    [InlineData("S1_04_SmokeTestDeploy.log")]
    [InlineData("S1_13_TPTest3.log")]
    public void Analyze_Should_NameTheTestDataSheet_When_TheTraceRecordsIt(string fixture)
    {
        var report = Analyze(fixture);

        Assert.Equal("WASSet1.xlsx", Path.GetFileName(report.DataWorkbook));
        Assert.Equal(Path.GetFileNameWithoutExtension(fixture), report.DataSheet);
    }

    [Fact]
    public void Analyze_Should_ReturnNull_When_TheTestPassed()
    {
        var report = new FirAnalyzer(FirFixtures.Packs()).Analyze(FirFixtures.Load("S1_21_GalaxyStatus.log"));

        Assert.Null(report);
    }

    [Fact]
    public void Analyze_Should_SummariseCauseAndEffect_When_RowsAreClassified()
    {
        var report = Analyze("S1_13_TPTest3.log");

        Assert.Contains("S1_13_TPTest3 failed (21 m 39 s, jvgr1).", report.Summary);
        Assert.Contains("Cause: row 23 `DeployOperation`", report.Summary);
        Assert.Contains("although the step logged Passed", report.Summary);
        Assert.Contains("Effect: rows 29-32 failed after it", report.Summary);
        // No sheet reader is supplied here, so the workbook is named but never located.
        Assert.Contains("test data (WASSet1.xlsx > S1_13_TPTest3, not found)", report.Summary);
        Assert.Contains("packs (generic, WASSmokeTest@1, AppServer@1)", report.Summary);
    }

    [Fact]
    public void Analyze_Should_FallBackToTheGenericLayer_When_NoPackApplies()
    {
        var unrelated = new FirTrxInput
        {
            TestName = "Total_Should_SumLines",
            ClassName = "Acme.Billing.Tests.InvoiceTests",
            Outcome = "Failed",
            ErrorMessage = "System.NullReferenceException: Object reference not set to an instance of an object.",
            StackTrace = "   at Acme.Billing.InvoiceCalculator.Total(Invoice i) in C:\\src\\InvoiceCalculator.cs:line 57",
        };

        var report = new FirAnalyzer(FirFixtures.Packs()).Analyze(unrelated)!;

        Assert.Equal(["generic"], report.RulePacks);
        Assert.Empty(report.Rows);
        Assert.Null(report.Root);
        Assert.Equal(FirFailureType.Exception, report.FailureType);
    }
}
