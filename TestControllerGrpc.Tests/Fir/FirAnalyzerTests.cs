using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// The generic layer runs before any rule pack, so every case here is deliberately expressed in
/// framework terms only. A product-specific expectation in this file would mean the layering leaked.
/// </summary>
public class FirAnalyzerTests
{
    private const string SummaryAssertionMessage =
        "Assert.IsFalse failed. TestCase FAILED and the Reason being: \"Validation Failure Count: 32, "
        + "Logger ERRORS Count: 1, Logger WARNINGS Count: 14\"";

    // A failure raised by TestCleanup over accumulated results - the shape the requirement calls out.
    private const string SummaryAssertionStack =
        """
           at Microsoft.VisualStudio.TestTools.UnitTesting.Assert.HandleFailure(String assertionName, String message)
           at Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(Boolean condition, String message)
           at WASSmokeTest.Base.TestBase.AssertTestPassed() in C:\src\WASSmokeTest\Base\TestBase.cs:line 142
           at WASSmokeTest.Base.TestBase.TestCleanup() in C:\src\WASSmokeTest\Base\TestBase.cs:line 98
        """;

    private const string StdOutWithNoiseAndError =
        """
        Warning: Test Run deployment issue: The assembly or module 'Foo' was not found.
        Test Run deployment issue: an error occurred while getting satellite assemblies for file 'Bar.dll'.
        Parameter name: startIndex
        [2026/10/05 02:38:40.001] Info: (Connect) Connected to galaxy.
        [2026/10/05 02:38:53.123] Failed: (DeployOperation) Deploy change operation is failed.
        Error: Failed to deploy REP1 : Platform startup failed.
        """;

    private static FirTrxInput SummaryCheckInput() => new()
    {
        TestName = "S1_04_SmokeTestDeploy",
        ClassName = "WASSmokeTest.Set1.S1_04_SmokeTestDeploy",
        Outcome = "Failed",
        Duration = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(57),
        ErrorMessage = SummaryAssertionMessage,
        StackTrace = SummaryAssertionStack,
        StdOut = StdOutWithNoiseAndError,
        Computer = "jvgr1",
    };

    private static FirTrxInput UnrelatedProductInput() => new()
    {
        TestName = "Total_Should_SumLines",
        ClassName = "Acme.Billing.Tests.InvoiceTests",
        Outcome = "Failed",
        Duration = TimeSpan.FromSeconds(4),
        ErrorMessage = "System.NullReferenceException: Object reference not set to an instance of an object.",
        StackTrace =
            """
               at Acme.Billing.InvoiceCalculator.Total(Invoice invoice) in C:\src\Acme\InvoiceCalculator.cs:line 57
               at Acme.Billing.Tests.InvoiceTests.Total_Should_SumLines() in C:\src\Acme.Tests\InvoiceTests.cs:line 22
            """,
    };

    [Theory]
    [InlineData("Passed")]
    [InlineData("NotExecuted")]
    [InlineData("Inconclusive")]
    [InlineData("")]
    public void Analyze_Should_ReturnNull_When_TestDidNotFail(string outcome)
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput() with { Outcome = outcome });

        Assert.Null(report);
    }

    [Fact]
    public void Analyze_Should_ReportSummaryCheck_When_AssertionIsRaisedUnderTestCleanup()
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput())!;

        Assert.Equal(FirFailureType.SummaryCheck, report.FailureType);
    }

    [Fact]
    public void Analyze_Should_PointAtTheOutput_When_FailureIsASummaryCheck()
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput())!;

        // The verdict must say the frame is not the cause, or a reader chases TestCleanup.
        Assert.Contains("not the cause", report.Verdict);
        Assert.Contains("Failed: (DeployOperation) Deploy change operation is failed.", report.Verdict);
    }

    [Fact]
    public void Analyze_Should_SkipHarnessFrames_When_ChoosingTheFailingFrame()
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput())!;

        Assert.Equal("AssertTestPassed", report.FailingFrame!.Method);
        Assert.Equal("TestBase.cs:142", report.FailingFrame.SourceFileLine);
    }

    [Fact]
    public void Analyze_Should_ClassifyTheFrameAsLibrary_When_ItIsOutsideTheTestClass()
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput())!;

        Assert.Equal(FirFrameKind.Library, report.FailingFrame!.Kind);
    }

    [Fact]
    public void Analyze_Should_ReportAssertion_When_ItIsRaisedInTheTestBody()
    {
        var input = UnrelatedProductInput() with
        {
            ErrorMessage = "Assert.AreEqual failed. Expected:<3>. Actual:<2>.",
            StackTrace =
                """
                   at Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual[T](T expected, T actual)
                   at Acme.Billing.Tests.InvoiceTests.Total_Should_SumLines() in C:\src\Acme.Tests\InvoiceTests.cs:line 22
                """,
        };

        var report = new FirAnalyzer().Analyze(input)!;

        Assert.Equal(FirFailureType.Assertion, report.FailureType);
        Assert.Equal(FirFrameKind.TestCode, report.FailingFrame!.Kind);
        Assert.Equal("Total_Should_SumLines", report.FailingFrame.Method);
    }

    [Fact]
    public void Analyze_Should_ProduceAGenericReport_When_TrxComesFromAnUnrelatedProduct()
    {
        var report = new FirAnalyzer().Analyze(UnrelatedProductInput())!;

        Assert.Equal(FirFailureType.Exception, report.FailureType);
        Assert.Equal("Total", report.FailingFrame!.Method);
        Assert.Equal("InvoiceCalculator.cs:57", report.FailingFrame.SourceFileLine);
        Assert.Contains("System.NullReferenceException", report.Verdict);
        Assert.Equal(["generic"], report.RulePacks);
    }

    [Fact]
    public void Analyze_Should_ReportTimeout_When_OutcomeIsTimeout()
    {
        var input = SummaryCheckInput() with
        {
            Outcome = "Timeout",
            Duration = TimeSpan.FromMinutes(30),
            ErrorMessage = null,
            StackTrace = null,
        };

        var report = new FirAnalyzer().Analyze(input)!;

        Assert.Equal(FirFailureType.Timeout, report.FailureType);
        Assert.Contains("30 m 0 s", report.Verdict);
    }

    [Fact]
    public void Analyze_Should_CountHarnessNoise_When_PickingTheFirstErrorLine()
    {
        var report = new FirAnalyzer().Analyze(SummaryCheckInput())!;

        Assert.Equal(3, report.NoiseLines);
        // The satellite-assemblies warning reads as an error line, comes first, and must still never win.
        Assert.Equal(
            "[2026/10/05 02:38:53.123] Failed: (DeployOperation) Deploy change operation is failed.",
            report.FirstErrorLine);
    }

    [Fact]
    public void Analyze_Should_ReportUnknown_When_TheTrxCarriesNoFailureText()
    {
        var input = SummaryCheckInput() with { ErrorMessage = null, StackTrace = null, StdOut = null };

        var report = new FirAnalyzer().Analyze(input)!;

        Assert.Equal(FirFailureType.Unknown, report.FailureType);
        Assert.Null(report.FailingFrame);
        Assert.NotEmpty(report.Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a stack trace at all")]
    [InlineData("at ")]
    [InlineData("\u0000\u0001 garbled")]
    public void Analyze_Should_NotThrow_When_OutputIsEmptyOrMalformed(string junk)
    {
        var input = new FirTrxInput
        {
            TestName = "T",
            Outcome = "Failed",
            ErrorMessage = junk,
            StackTrace = junk,
            StdOut = junk,
            DebugTrace = junk,
        };

        var report = new FirAnalyzer().Analyze(input);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Verdict);
    }

    [Fact]
    public void FromTestResult_Should_CarryEveryOutputStream_When_AdaptingATrxResult()
    {
        var result = new TestResult
        {
            TestName = "S1_13_TPTest3",
            ClassName = "WASSmokeTest.Set1.S1_13_TPTest3",
            Outcome = "Failed",
            Duration = TimeSpan.FromMinutes(21),
            ErrorMessage = SummaryAssertionMessage,
            StackTrace = SummaryAssertionStack,
            StdOut = "stdout text",
            DebugTrace = "debug trace text",
        };

        var input = FirTrxInput.FromTestResult(result, computer: "jvgr1", logText: "log text");

        Assert.Equal(["stdout text", "debug trace text", "log text"], input.OutputSections());
        Assert.Equal("jvgr1", input.Computer);
        Assert.Contains(SummaryAssertionMessage, input.AllText());
    }

    [Fact]
    public void Parse_Should_RecoverTheOriginalMethod_When_TheFrameIsAnAsyncStateMachine()
    {
        var frames = FirStackTrace.Parse("   at Acme.Worker+<RunAsync>d__12.MoveNext() in C:\\src\\Worker.cs:line 9");

        Assert.Equal("RunAsync", frames.Single().Method);
        Assert.Equal("Acme.Worker", frames.Single().DeclaringType);
    }
}
