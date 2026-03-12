using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ExecutionSessionManagerTests
{
    private ExecutionSessionManager CreateManager() => new();

    private ExecutionSession BeginTestSession(
        ExecutionSessionManager mgr, string tag = "TestItem", string eventType = "Renamed")
    {
        return mgr.BeginSession(tag, eventType,
            new Dictionary<string, string> { ["BuildNumber"] = "1.0" },
            new List<IActionNode> { new ActionConfig { Command = "echo" } });
    }

    // ???????????????????????????????????????????????????????????????????
    // BeginSession
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void BeginSession_Should_CreateRunningSession_When_Called()
    {
        var mgr = CreateManager();

        var session = BeginTestSession(mgr);

        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal("TestItem", session.WatchItemTag);
        Assert.Equal("Renamed", session.EventType);
        Assert.NotEmpty(session.SessionId);
    }

    [Fact]
    public void BeginSession_Should_TrackAsActive_When_NotCompleted()
    {
        var mgr = CreateManager();

        var session = BeginTestSession(mgr);

        Assert.True(mgr.HasAnyActiveExecution);
        Assert.True(mgr.HasActiveExecution("TestItem"));
        Assert.Equal(1, mgr.ActiveExecutionCount);
    }

    [Fact]
    public void BeginSession_Should_CopyParameters_When_Provided()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);

        Assert.Equal("1.0", session.ResolvedParameters["BuildNumber"]);
    }

    // ???????????????????????????????????????????????????????????????????
    // RecordResult
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void RecordResult_Should_AddToSession_When_SessionIsActive()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);

        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "action1",
            Outcome = ActionOutcome.Success,
        });

        Assert.Equal(1, session.TotalActions);
        Assert.Equal(1, session.SucceededCount);
    }

    [Fact]
    public void RecordResult_Should_DoNothing_When_SessionIdInvalid()
    {
        var mgr = CreateManager();

        // Should not throw
        mgr.RecordResult("nonexistent-id", new ActionExecutionResult
        {
            ActionTag = "orphan",
            Outcome = ActionOutcome.Success,
        });
    }

    // ???????????????????????????????????????????????????????????????????
    // CompleteSession
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void CompleteSession_Should_SetCompletedState_When_AllActionsSucceeded()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);
        mgr.RecordResult(session.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Success });

        mgr.CompleteSession(session.SessionId);

        Assert.False(mgr.HasAnyActiveExecution);
        Assert.Equal(SessionState.Completed, session.State);
        Assert.NotNull(session.CompletedUtc);
    }

    [Fact]
    public void CompleteSession_Should_SetFailedState_When_AllActionsFailed()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);
        mgr.RecordResult(session.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Failed });

        mgr.CompleteSession(session.SessionId);

        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public void CompleteSession_Should_SetPartialFailure_When_MixedResults()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);
        mgr.RecordResult(session.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Success });
        mgr.RecordResult(session.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Failed });

        mgr.CompleteSession(session.SessionId);

        Assert.Equal(SessionState.PartialFailure, session.State);
    }

    [Fact]
    public void CompleteSession_Should_RemoveFromActive_When_Completed()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr);

        mgr.CompleteSession(session.SessionId);

        Assert.False(mgr.HasActiveExecution("TestItem"));
        Assert.Equal(0, mgr.ActiveExecutionCount);
    }

    // ???????????????????????????????????????????????????????????????????
    // GetLastSession
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void GetLastSession_Should_ReturnMostRecent_When_MultipleSessionsCompleted()
    {
        var mgr = CreateManager();

        var s1 = mgr.BeginSession("Item1", "Renamed",
            new Dictionary<string, string> { ["Build"] = "1" }, []);
        mgr.RecordResult(s1.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Success });
        mgr.CompleteSession(s1.SessionId);

        var s2 = mgr.BeginSession("Item1", "Renamed",
            new Dictionary<string, string> { ["Build"] = "2" }, []);
        mgr.RecordResult(s2.SessionId, new ActionExecutionResult { Outcome = ActionOutcome.Failed });
        mgr.CompleteSession(s2.SessionId);

        var last = mgr.GetLastSession("Item1");
        Assert.NotNull(last);
        Assert.Equal(s2.SessionId, last.SessionId);
    }

    [Fact]
    public void GetLastSession_Should_ReturnNull_When_NoSessionsExist()
    {
        var mgr = CreateManager();
        Assert.Null(mgr.GetLastSession("nonexistent"));
    }

    [Fact]
    public void GetLastSession_Should_BeCaseInsensitive_When_LookingUpTag()
    {
        var mgr = CreateManager();
        var session = BeginTestSession(mgr, "MyItem");
        mgr.CompleteSession(session.SessionId);

        Assert.NotNull(mgr.GetLastSession("myitem"));
        Assert.NotNull(mgr.GetLastSession("MYITEM"));
    }

    // ???????????????????????????????????????????????????????????????????
    // HasActiveExecution
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void HasActiveExecution_Should_ReturnFalse_When_NoSessionsRunning()
    {
        var mgr = CreateManager();
        Assert.False(mgr.HasActiveExecution("anything"));
    }

    [Fact]
    public void HasActiveExecution_Should_BeCaseInsensitive_When_CheckingTag()
    {
        var mgr = CreateManager();
        BeginTestSession(mgr, "MyCasedItem");

        Assert.True(mgr.HasActiveExecution("mycaseditem"));
    }

    // ???????????????????????????????????????????????????????????????????
    // ExecutionSession model
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ExecutionSession_Should_CountRetryableActions_When_HasFailedAndTimedOut()
    {
        var session = new ExecutionSession { WatchItemTag = "Test" };
        session.ActionResults.Add(new ActionExecutionResult { Outcome = ActionOutcome.Success });
        session.ActionResults.Add(new ActionExecutionResult { Outcome = ActionOutcome.Failed });
        session.ActionResults.Add(new ActionExecutionResult { Outcome = ActionOutcome.TimedOut });
        session.ActionResults.Add(new ActionExecutionResult { Outcome = ActionOutcome.Terminated });

        Assert.Equal(4, session.TotalActions);
        Assert.Equal(1, session.SucceededCount);
        Assert.Equal(3, session.FailedCount);
        Assert.Equal(3, session.FailedActions.Count());
    }

    [Fact]
    public void ActionExecutionResult_Should_FormatDuration_When_ShortDuration()
    {
        var result = new ActionExecutionResult { Duration = TimeSpan.FromMilliseconds(500) };
        Assert.Contains("ms", result.DurationText);

        result = new ActionExecutionResult { Duration = TimeSpan.FromSeconds(5.5) };
        Assert.Contains("s", result.DurationText);

        result = new ActionExecutionResult { Duration = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30) };
        Assert.Contains(":", result.DurationText);
    }

    [Fact]
    public void ActionExecutionResult_Should_ShowCorrectStatusIcon_When_OutcomeVaries()
    {
        Assert.Equal("\u2713", new ActionExecutionResult { Outcome = ActionOutcome.Success }.StatusIcon);  // ?
        Assert.Equal("\u2717", new ActionExecutionResult { Outcome = ActionOutcome.Failed }.StatusIcon);   // ?
        Assert.Equal("\u2298", new ActionExecutionResult { Outcome = ActionOutcome.Terminated }.StatusIcon); // ?
        Assert.Equal("\u23F1", new ActionExecutionResult { Outcome = ActionOutcome.TimedOut }.StatusIcon); // ?
        Assert.Equal("\u2026", new ActionExecutionResult { Outcome = ActionOutcome.Unknown }.StatusIcon);  // …
    }
}
