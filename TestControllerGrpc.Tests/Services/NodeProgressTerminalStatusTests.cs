using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Guards the rule that makes node status trustworthy: the LAST per-node message a run emits must be
/// a terminal state.
/// </summary>
/// <remarks>
/// The UI keeps showing whatever a node reported last. A non-terminal final status therefore leaves the
/// node pulsing forever with no pass or fail — which is exactly what <c>ActionOutcome.Skipped</c> and
/// <c>Unknown</c> did when <c>MapOutcome</c> fell through to "Running".
/// </remarks>
public class NodeProgressTerminalStatusTests
{
    private sealed class RecordingAggregator : IEventAggregator
    {
        public List<NodeProgressEvent> Progress { get; } = [];

        public void Publish<TEvent>(TEvent evt)
        {
            if (evt is NodeProgressEvent p) Progress.Add(p);
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static readonly string[] Terminal = ["Success", "Failed", "Skipped", "Cancelled"];

    [Theory]
    [InlineData(ActionOutcome.Success)]
    [InlineData(ActionOutcome.Failed)]
    [InlineData(ActionOutcome.Terminated)]
    [InlineData(ActionOutcome.TimedOut)]
    [InlineData(ActionOutcome.Skipped)]
    [InlineData(ActionOutcome.Unknown)]
    public void RecordResult_Should_PublishATerminalStatus_When_AnyOutcomeIsRecorded(ActionOutcome outcome)
    {
        var events = new RecordingAggregator();
        var mgr = new ExecutionSessionManager(events);
        var session = mgr.BeginSession("Pipe", "Renamed", [], [new ActionConfig { Command = "echo" }]);

        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "step-1",
            Outcome = outcome,
        });

        var published = Assert.Single(events.Progress);
        Assert.Contains(published.Status, Terminal);
    }

    /// <summary>
    /// Every enum member must be covered, so adding a new outcome without deciding its UI status
    /// fails here instead of silently stranding a node as "Running" in production.
    /// </summary>
    [Fact]
    public void RecordResult_Should_CoverEveryOutcome_When_TheEnumGrows()
    {
        foreach (var outcome in Enum.GetValues<ActionOutcome>())
        {
            var events = new RecordingAggregator();
            var mgr = new ExecutionSessionManager(events);
            var session = mgr.BeginSession("Pipe", "Renamed", [], [new ActionConfig { Command = "echo" }]);

            mgr.RecordResult(session.SessionId, new ActionExecutionResult
            {
                ActionTag = "step-1",
                Outcome = outcome,
            });

            var status = events.Progress.Single().Status;
            Assert.True(Terminal.Contains(status),
                $"ActionOutcome.{outcome} maps to '{status}', which is not terminal — a node that "
                + "ends on it would pulse forever and never report pass or fail.");
        }
    }

    [Fact]
    public void BeginAction_Should_PublishRunning_When_ActionStarts()
    {
        var events = new RecordingAggregator();
        var mgr = new ExecutionSessionManager(events);
        var session = mgr.BeginSession("Pipe", "Renamed", [], [new ActionConfig { Command = "echo" }]);

        mgr.BeginAction(session.SessionId, new ActionExecutionResult { ActionTag = "step-1" });

        Assert.Equal("Running", events.Progress.Single().Status);
    }

    [Fact]
    public void PublishNodeProgress_Should_CarrySessionId_When_ContainerNodeReports()
    {
        var events = new RecordingAggregator();
        var mgr = new ExecutionSessionManager(events);
        var session = mgr.BeginSession("Pipe", "Renamed", [], []);

        mgr.PublishNodeProgress(session.SessionId, "Phase1", "ActionGroup", "Running");

        var published = Assert.Single(events.Progress);
        Assert.Equal(session.SessionId, published.SessionId);
        Assert.Equal("Phase1", published.NodeTag);
    }
}
