using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Tests for FailureAnalysisVM – computed properties, ToClipboardText(),
/// BuildHistoryCell mapping, and SignatureRow null-coalescing.
/// </summary>
public class FailureAnalysisVMTests
{
    private static FailureAnalysisReport MakeReport(
        FailurePattern pattern = FailurePattern.NewFailure,
        int confidence = 85)
    {
        return new FailureAnalysisReport
        {
            TestCaseName = "TestLogin",
            Pattern = pattern,
            Verdict = "Likely a new regression",
            Confidence = confidence,
            SuggestedAction = "Investigate recent changes",
            History = new List<TestExecutionRecord>
            {
                new() { BuildName = "Build-10", Outcome = "Failed",
                    BuildDate = new DateTime(2025, 1, 15, 10, 0, 0) },
                new() { BuildName = "Build-9", Outcome = "Passed",
                    BuildDate = new DateTime(2025, 1, 14, 10, 0, 0) },
            },
            FailureSignatures = new List<FailureSignature>
            {
                new()
                {
                    BuildName = "Build-10",
                    FailedStepIndex = 3,
                    FailedStepName = "RunTests",
                    ErrorType = "AssertionError",
                    NormalizedMessage = "Expected true but got false",
                    Agent = "Agent-01",
                },
            },
        };
    }

    [Fact]
    public void TestCaseName_Should_MirrorReport()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Equal("TestLogin", vm.TestCaseName);
    }

    [Fact]
    public void PatternLabel_Should_ReturnEnumName()
    {
        var vm = new FailureAnalysisVM(MakeReport(FailurePattern.FlakyTest));
        Assert.Equal("FlakyTest", vm.PatternLabel);
    }

    [Fact]
    public void ConfidenceText_Should_FormatPercent()
    {
        var vm = new FailureAnalysisVM(MakeReport(confidence: 92));
        Assert.Equal("92%", vm.ConfidenceText);
    }

    [Fact]
    public void LatestBuildName_Should_ReturnFirstHistoryEntry()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Equal("Build-10", vm.LatestBuildName);
    }

    [Fact]
    public void LatestBuildName_Should_ReturnNull_When_HistoryEmpty()
    {
        var report = MakeReport();
        report.History.Clear();
        var vm = new FailureAnalysisVM(report);
        Assert.Null(vm.LatestBuildName);
    }

    [Fact]
    public void LatestFailedStepIndex_Should_ReturnFirstSignatureIndex()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Equal(3, vm.LatestFailedStepIndex);
    }

    [Fact]
    public void LatestFailedStepIndex_Should_ReturnMinusOne_When_NoSignatures()
    {
        var report = MakeReport();
        report.FailureSignatures.Clear();
        var vm = new FailureAnalysisVM(report);
        Assert.Equal(-1, vm.LatestFailedStepIndex);
    }

    // ─────────── History cells ───────────

    [Fact]
    public void History_Should_MapOutcomeToCells()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Equal(2, vm.History.Count);
        Assert.Equal("Failed", vm.History[0].Outcome);
        Assert.Equal("\u2717", vm.History[0].OutcomeChar);
        Assert.Equal("Passed", vm.History[1].Outcome);
        Assert.Equal("\u2713", vm.History[1].OutcomeChar);
    }

    [Fact]
    public void HistoryCell_ToolTip_Should_ContainBuildNameAndOutcome()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Contains("Build-10", vm.History[0].ToolTipText);
        Assert.Contains("Failed", vm.History[0].ToolTipText);
    }

    // ─────────── Signature rows ───────────

    [Fact]
    public void SignatureRow_Should_MapFields()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        Assert.Single(vm.FailureSignatures);
        Assert.Equal("RunTests", vm.FailureSignatures[0].FailedStepName);
        Assert.Equal("Agent-01", vm.FailureSignatures[0].Agent);
    }

    [Fact]
    public void SignatureRow_Should_UseDash_When_FieldsEmpty()
    {
        var report = MakeReport();
        report.FailureSignatures = new List<FailureSignature>
        {
            new() { BuildName = "B1", FailedStepName = "", ErrorType = "", Agent = "" }
        };
        var vm = new FailureAnalysisVM(report);
        var row = vm.FailureSignatures[0];
        Assert.Equal("\u2014", row.FailedStepName);
        Assert.Equal("\u2014", row.ErrorType);
        Assert.Equal("\u2014", row.Agent);
    }

    // ─────────── ToClipboardText ───────────

    [Fact]
    public void ToClipboardText_Should_ContainTestCaseName()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        var text = vm.ToClipboardText();
        Assert.Contains("TestLogin", text);
    }

    [Fact]
    public void ToClipboardText_Should_ContainPattern()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        var text = vm.ToClipboardText();
        Assert.Contains("NewFailure", text);
    }

    [Fact]
    public void ToClipboardText_Should_ContainBuildHistory()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        var text = vm.ToClipboardText();
        Assert.Contains("Build-10", text);
        Assert.Contains("Build-9", text);
    }

    [Fact]
    public void ToClipboardText_Should_ContainSuggestedAction()
    {
        var vm = new FailureAnalysisVM(MakeReport());
        var text = vm.ToClipboardText();
        Assert.Contains("Investigate recent changes", text);
    }

    // ─────────── VerdictBrush (pattern-switch mapping) ───────────

    [Theory]
    [InlineData(FailurePattern.SystemicRegression)]
    [InlineData(FailurePattern.CascadingFailures)]
    [InlineData(FailurePattern.FlakyTest)]
    [InlineData(FailurePattern.ChronicFailure)]
    [InlineData(FailurePattern.Resolved)]
    [InlineData(FailurePattern.NewFailure)]
    public void VerdictBorderBrush_Should_NotBeNull_ForAllPatterns(FailurePattern pattern)
    {
        var vm = new FailureAnalysisVM(MakeReport(pattern));
        Assert.NotNull(vm.VerdictBorderBrush);
        Assert.NotNull(vm.VerdictForegroundBrush);
    }
}
