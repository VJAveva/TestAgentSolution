using System.Windows;
using static TestControllerGrpc.ViewModels.ExecutionLogViewerVM;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Tests for ExecutionLogViewerVM – filtering, computed properties,
/// TimelineRow mapping, and source/severity toggle logic.
/// </summary>
public class ExecutionLogViewerVMTests
{
    private static LogPayload MakePayload(
        string outcome = "Failed",
        int failedStepIndex = 2,
        string errorMessage = "Assertion failed",
        List<MergedLineDto>? timeline = null)
    {
        return new LogPayload
        {
            BuildName = "Build-42",
            TestCaseName = "TestLogin",
            Outcome = outcome,
            Agent = "Agent-01",
            StartTime = "10:00:00",
            EndTime = "10:05:00",
            Duration = "5m 0s",
            FailedStepIndex = failedStepIndex,
            ErrorMessage = errorMessage,
            StackTrace = "at Tests.Login()",
            MergedTimeline = timeline ?? MakeDefaultTimeline(),
        };
    }

    private static List<MergedLineDto> MakeDefaultTimeline() => new()
    {
        new() { Timestamp = DateTime.Today.AddHours(10), Source = "TRX", Severity = "Info", Message = "Test started" },
        new() { Timestamp = DateTime.Today.AddHours(10).AddMinutes(1), Source = "Agent", Severity = "Info", Message = "Running step 1" },
        new() { Timestamp = DateTime.Today.AddHours(10).AddMinutes(2), Source = "Agent", Severity = "Error", Message = "Step 2 failed" },
        new() { Timestamp = DateTime.Today.AddHours(10).AddMinutes(3), Source = "Controller", Severity = "Warning", Message = "Retry recommended" },
        new() { Timestamp = DateTime.Today.AddHours(10).AddMinutes(4), Source = "TRX", Severity = "Info", Message = "Test completed" },
    };

    // ═══════════════════════════════════════════════════════════════════
    // Basic properties
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Ctor_Should_PopulateAllFields()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        Assert.Equal("Build-42", vm.BuildName);
        Assert.Equal("TestLogin", vm.TestCaseName);
        Assert.Equal("Failed", vm.Outcome);
        Assert.Equal("Agent-01", vm.Agent);
        Assert.Equal("10:00:00", vm.StartTime);
        Assert.Equal("10:05:00", vm.EndTime);
        Assert.Equal("5m 0s", vm.Duration);
        Assert.Equal(2, vm.FailedStepIndex);
        Assert.Equal("Assertion failed", vm.ErrorMessage);
    }

    [Fact]
    public void FailedStepDisplay_Should_ShowStepNumber_When_Positive()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        Assert.Equal("Step #2", vm.FailedStepDisplay);
    }

    [Fact]
    public void FailedStepDisplay_Should_ShowDash_When_NegativeIndex()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(
            MakePayload(failedStepIndex: -1));
        Assert.Equal("\u2014", vm.FailedStepDisplay);
    }

    [Fact]
    public void ErrorBlockVisibility_Should_BeVisible_When_HasError()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        Assert.Equal(Visibility.Visible, vm.ErrorBlockVisibility);
    }

    [Fact]
    public void ErrorBlockVisibility_Should_BeCollapsed_When_NoError()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(
            MakePayload(errorMessage: ""));
        Assert.Equal(Visibility.Collapsed, vm.ErrorBlockVisibility);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Timeline count
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TimelineCountText_Should_ShowAllByDefault()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        Assert.Equal("5 of 5 lines", vm.TimelineCountText);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Filtering – source toggles
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DisableTrx_Should_HideTrxRows()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.ShowTrx = false;
        Assert.Equal(3, vm.FilteredTimeline.Count); // Agent(2) + Controller(1)
        Assert.DoesNotContain(vm.FilteredTimeline, r => r.Source == "TRX");
    }

    [Fact]
    public void DisableAgent_Should_HideAgentRows()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.ShowAgent = false;
        Assert.Equal(3, vm.FilteredTimeline.Count); // TRX(2) + Controller(1)
        Assert.DoesNotContain(vm.FilteredTimeline, r => r.Source == "Agent");
    }

    [Fact]
    public void DisableController_Should_HideControllerRows()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.ShowController = false;
        Assert.Equal(4, vm.FilteredTimeline.Count); // TRX(2) + Agent(2)
    }

    // ═══════════════════════════════════════════════════════════════════
    // Filtering – errors only
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ErrorsOnly_Should_ShowOnlyErrorRows()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.ErrorsOnly = true;
        Assert.Single(vm.FilteredTimeline);
        Assert.Equal("Step 2 failed", vm.FilteredTimeline[0].Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Filtering – search text
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SearchText_Should_FilterByMessage()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.SearchText = "step 2";
        Assert.Single(vm.FilteredTimeline);
        Assert.Contains("Step 2 failed", vm.FilteredTimeline[0].Message);
    }

    [Fact]
    public void SearchText_Should_BeCaseInsensitive()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.SearchText = "RETRY";
        Assert.Single(vm.FilteredTimeline);
    }

    [Fact]
    public void ClearSearchText_Should_RestoreAll()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.SearchText = "nonexistent";
        Assert.Empty(vm.FilteredTimeline);
        vm.SearchText = "";
        Assert.Equal(5, vm.FilteredTimeline.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Combined filters
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CombinedFilters_Should_ApplyTogether()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        vm.ShowTrx = false;
        vm.ErrorsOnly = true;
        // Only Agent error row remains
        Assert.Single(vm.FilteredTimeline);
        Assert.Equal("Agent", vm.FilteredTimeline[0].Source);
        Assert.Equal("Error", vm.FilteredTimeline[0].Severity);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TimelineRow mapping
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TimelineRow_Should_FormatTimeText()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        var first = vm.AllTimeline[0];
        Assert.Equal(DateTime.Today.AddHours(10).ToString("HH:mm:ss.fff"), first.TimeText);
    }

    [Fact]
    public void TimelineRow_SourceBrush_Should_NotBeNull()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(MakePayload());
        foreach (var row in vm.AllTimeline)
        {
            Assert.NotNull(row.SourceBrush);
            Assert.NotNull(row.SeverityBrush);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Null payload handling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void NullTimeline_Should_ProduceEmptyList()
    {
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(
            MakePayload(timeline: null!));
        // MakePayload already handles null via default, but let's test explicit null
        var payload = new LogPayload { MergedTimeline = null };
        var vm2 = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(payload);
        Assert.Empty(vm2.AllTimeline);
        Assert.Empty(vm2.FilteredTimeline);
    }

    [Fact]
    public void NullFields_Should_DefaultToEmpty()
    {
        var payload = new LogPayload();
        var vm = new TestControllerGrpc.ViewModels.ExecutionLogViewerVM(payload);
        Assert.Equal("", vm.BuildName);
        Assert.Equal("", vm.TestCaseName);
        Assert.Equal("", vm.Outcome);
        Assert.Equal("", vm.ErrorMessage);
    }
}
