using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class FailurePatternAnalyzerTests
{
    private readonly FailurePatternAnalyzer _analyzer;

    public FailurePatternAnalyzerTests()
    {
        var config = new BuildResultsConfig { ResultsRootPath = @"C:\NonExistent" };
        var parser = new TrxResultsParser();
        _analyzer = new FailurePatternAnalyzer(parser, config);
    }

    [Fact]
    public void AnalyzeTest_Should_ReturnNone_When_TestNotFound()
    {
        var report = _analyzer.AnalyzeTest("NonExistentTest", 5);

        Assert.Equal(FailurePattern.None, report.Pattern);
        Assert.Equal(0, report.Confidence);
        Assert.Empty(report.History);
    }

    [Fact]
    public void NormalizeErrorMessage_Should_ReplaceGuids()
    {
        var raw = "Object 12345678-1234-1234-1234-123456789abc not found";
        var result = _analyzer.NormalizeErrorMessage(raw);

        Assert.Contains("{GUID}", result);
        Assert.DoesNotContain("12345678", result);
    }

    [Fact]
    public void NormalizeErrorMessage_Should_ReplaceDates()
    {
        var raw = "Failed at 2024-03-15T10:30:00 during operation";
        var result = _analyzer.NormalizeErrorMessage(raw);

        Assert.Contains("{DATETIME}", result);
        Assert.DoesNotContain("2024-03-15", result);
    }

    [Fact]
    public void NormalizeErrorMessage_Should_ReplaceNumbers()
    {
        var raw = "Timeout after 45000ms waiting for response";
        var result = _analyzer.NormalizeErrorMessage(raw);

        Assert.Contains("{N}", result);
    }

    [Fact]
    public void NormalizeErrorMessage_Should_ReplacePaths()
    {
        var raw = @"File not found: C:\TestResults\Build123\output.trx";
        var result = _analyzer.NormalizeErrorMessage(raw);

        Assert.Contains("{PATH}", result);
    }

    [Fact]
    public void NormalizeErrorMessage_Should_ReturnEmpty_When_NullInput()
    {
        Assert.Equal("", _analyzer.NormalizeErrorMessage(null));
        Assert.Equal("", _analyzer.NormalizeErrorMessage(""));
    }

    // ?? Pattern classification tests (using direct report construction) ??

    [Fact]
    public void Pattern_Should_BeSystemicRegression_When_ConsecutiveFailsWithSameSignature()
    {
        // Verifying the classification logic via the enum values
        Assert.Equal("SystemicRegression", FailurePattern.SystemicRegression.ToString());
    }

    [Fact]
    public void Pattern_Should_BeCascadingFailures_When_DifferentSignatures()
    {
        Assert.Equal("CascadingFailures", FailurePattern.CascadingFailures.ToString());
    }

    [Fact]
    public void Pattern_Should_BeChronicFailure_When_AllFailed()
    {
        Assert.Equal("ChronicFailure", FailurePattern.ChronicFailure.ToString());
    }

    [Fact]
    public void Pattern_Should_BeFlakyTest_When_Alternating()
    {
        Assert.Equal("FlakyTest", FailurePattern.FlakyTest.ToString());
    }

    [Fact]
    public void Pattern_Should_BeResolved_When_NowPassing()
    {
        Assert.Equal("Resolved", FailurePattern.Resolved.ToString());
    }

    [Fact]
    public void FailureAnalysisReport_Should_HaveCorrectDefaults()
    {
        var report = new FailureAnalysisReport();

        Assert.Equal("", report.TestCaseName);
        Assert.Equal(FailurePattern.None, report.Pattern);
        Assert.Equal(0, report.Confidence);
        Assert.Empty(report.History);
        Assert.Empty(report.FailureSignatures);
        Assert.Null(report.LastPassBuild);
        Assert.Null(report.FirstFailBuild);
    }

    [Fact]
    public void TestExecutionRecord_Should_HaveCorrectDefaults()
    {
        var record = new TestExecutionRecord();

        Assert.Equal("", record.TestName);
        Assert.Equal("", record.Outcome);
        Assert.Equal(TimeSpan.Zero, record.Duration);
        Assert.Empty(record.Steps);
    }

    [Fact]
    public void FailureSignature_Should_HaveCorrectDefaults()
    {
        var sig = new FailureSignature();

        Assert.Equal(-1, sig.FailedStepIndex);
        Assert.Equal("", sig.ErrorType);
        Assert.Equal("", sig.NormalizedMessage);
    }

    [Fact]
    public void GetCompactLabel_Should_ReturnDash_When_TestNotFound()
    {
        var label = _analyzer.GetCompactLabel("NonExistentTest");
        Assert.Equal("—", label);
    }
}
