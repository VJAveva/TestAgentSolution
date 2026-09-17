using TestAgentDisplay.ViewModels;
using TestAgentGrpc;
using Xunit;

namespace TestAgentDisplay.Tests;

/// <summary>
/// Characterization tests for the node view model's event handling. These pin behaviour that was
/// previously unprotected: TestAgentDisplay had no test project at all before 2026-09-17.
/// </summary>
public class AgentNodeViewModelTests
{
    private static ExecutionEvent Event(ExecutionEventType type) => new() { EventType = type };

    // â”€â”€ Windows Update posture (EVENT_WINDOWS_UPDATE) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void HandleEvent_Should_SurfacePosture_When_WindowsUpdateEventArrives()
    {
        var vm = new AgentNodeViewModel();
        var evt = Event(ExecutionEventType.EventWindowsUpdate);
        evt.Detail = """{ "rebootRequired": true, "pendingCount": 4, "detectedUtc": "2026-09-17T06:30:00+00:00" }""";

        vm.HandleEvent(evt);

        Assert.True(vm.HasUpdatePosture);
        Assert.True(vm.RebootRequired);
        Assert.Equal(4, vm.PendingUpdateCount);
        Assert.Equal("Reboot required (4 pending)", vm.UpdateSummary);
        Assert.NotEqual("", vm.UpdateCheckedAt);
    }

    [Fact]
    public void HandleEvent_Should_NotClaimPosture_When_PayloadIsMalformed()
    {
        var vm = new AgentNodeViewModel();
        var evt = Event(ExecutionEventType.EventWindowsUpdate);
        evt.Detail = "{ this is not json";

        vm.HandleEvent(evt);

        // Must not silently look "up to date" when we actually failed to read the payload.
        Assert.False(vm.HasUpdatePosture);
        Assert.Contains(vm.OutputLines, l => l.Kind == OutputLineKind.Warn && l.Text.Contains("could not be parsed"));
    }

    [Fact]
    public void HandleEvent_Should_ListFailedUpdates_When_ItemsIncludeFailures()
    {
        var vm = new AgentNodeViewModel();
        var evt = Event(ExecutionEventType.EventWindowsUpdate);
        evt.Detail = """
        {
          "rebootRequired": false,
          "pendingCount": 1,
          "items": [ { "kbId": "KB5034999", "title": "Security Update", "result": "Failed", "resultCode": "0x80073712" } ]
        }
        """;

        vm.HandleEvent(evt);

        var failure = Assert.Single(vm.OutputLines, l => l.Kind == OutputLineKind.Error);
        Assert.Contains("KB5034999", failure.Text);
        Assert.Contains("0x80073712", failure.Text);
    }

    [Fact]
    public void HandleEvent_Should_ReportUpToDate_When_NothingPending()
    {
        var vm = new AgentNodeViewModel();
        var evt = Event(ExecutionEventType.EventWindowsUpdate);
        evt.Detail = """{ "rebootRequired": false, "pendingCount": 0 }""";

        vm.HandleEvent(evt);

        Assert.True(vm.HasUpdatePosture);
        Assert.Equal("Up to date", vm.UpdateSummary);
    }

    // â”€â”€ State transitions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(AgentState.Ready, AgentDisplayState.Ready, "Ready")]
    [InlineData(AgentState.Running, AgentDisplayState.Running, "Running")]
    [InlineData(AgentState.Inactive, AgentDisplayState.Inactive, "Inactive")]
    public void HandleEvent_Should_MapAgentState_When_StateChangedArrives(
        AgentState reported, AgentDisplayState expectedKind, string expectedText)
    {
        var vm = new AgentNodeViewModel();
        var evt = Event(ExecutionEventType.EventStateChanged);
        evt.AgentState = reported;

        vm.HandleEvent(evt);

        Assert.Equal(expectedKind, vm.StateKind);
        Assert.Equal(expectedText, vm.StateText);
    }

    [Fact]
    public void SetConnected_Should_GoOffline_When_Disconnected()
    {
        var vm = new AgentNodeViewModel();
        vm.SetConnected(true);
        vm.SetConnected(false);

        Assert.Equal(AgentDisplayState.Offline, vm.StateKind);
        Assert.Equal("Offline", vm.StateText);
        Assert.Equal("", vm.CurrentCommand);
    }

    [Fact]
    public void HandleEvent_Should_ClearExecutionId_When_RunCompletes()
    {
        var vm = new AgentNodeViewModel();

        // The execution id is stamped by EventQueued, not EventStarted - the real agent flow is
        // Queued -> Started -> Completed, and only Queued carries it.
        var queued = Event(ExecutionEventType.EventQueued);
        queued.ExecutionId = "abc123";
        vm.HandleEvent(queued);
        Assert.Equal("abc123", vm.CurrentExecutionId);

        var started = Event(ExecutionEventType.EventStarted);
        started.ExecutionId = "abc123";
        started.Command = "run.bat";
        vm.HandleEvent(started);
        Assert.Equal("run.bat ", vm.CurrentCommand);

        var completed = Event(ExecutionEventType.EventCompleted);
        completed.ExecutionId = "abc123";
        completed.ExitCode = 0;
        vm.HandleEvent(completed);

        // DISPLAY-002: a stale execution id left on screen is the bug this guards.
        Assert.Equal("", vm.CurrentExecutionId);
        Assert.Equal("", vm.CurrentCommand);
        Assert.Equal(1, vm.CompletedCount);
    }

    [Theory]
    [InlineData(0, OutputLineKind.Success)]
    [InlineData(1, OutputLineKind.Error)]
    public void HandleEvent_Should_ColourExitByOutcome_When_RunCompletes(int exitCode, OutputLineKind expected)
    {
        var vm = new AgentNodeViewModel();
        var completed = Event(ExecutionEventType.EventCompleted);
        completed.ExitCode = exitCode;

        vm.HandleEvent(completed);

        Assert.Contains(vm.OutputLines, l => l.Kind == expected && l.Text.Contains("Exit code"));
    }

    [Fact]
    public void HandleEvent_Should_CountFailures_When_RunFails()
    {
        var vm = new AgentNodeViewModel();
        var failed = Event(ExecutionEventType.EventFailed);
        failed.ErrorMessage = "boom";

        vm.HandleEvent(failed);

        Assert.Equal(1, vm.FailedCount);
        Assert.Equal(AgentDisplayState.Ready, vm.StateKind);
        Assert.Contains(vm.OutputLines, l => l.Kind == OutputLineKind.Error && l.Text.Contains("boom"));
    }

    [Fact]
    public void HandleEvent_Should_IgnoreUnknownEventTypes_When_ContractAddsNewKinds()
    {
        var vm = new AgentNodeViewModel();

        // A future agent sending an event this build has never heard of must not throw.
        var unknown = Event((ExecutionEventType)999);
        var ex = Record.Exception(() => vm.HandleEvent(unknown));

        Assert.Null(ex);
    }

    // â”€â”€ Output buffer â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void HandleEvent_Should_MapStreamsToKinds_When_OutputArrives()
    {
        var vm = new AgentNodeViewModel();

        var stdout = Event(ExecutionEventType.EventStdoutLine);
        stdout.OutputLine = "hello";
        vm.HandleEvent(stdout);

        var stderr = Event(ExecutionEventType.EventStderrLine);
        stderr.OutputLine = "uh oh";
        vm.HandleEvent(stderr);

        Assert.Contains(vm.OutputLines, l => l.Kind == OutputLineKind.Stdout && l.Text.Contains("hello"));
        Assert.Contains(vm.OutputLines, l => l.Kind == OutputLineKind.Warn && l.Text.Contains("uh oh"));
    }
}
