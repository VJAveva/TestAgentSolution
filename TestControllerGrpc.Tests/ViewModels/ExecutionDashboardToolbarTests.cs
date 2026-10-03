using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Dashboard toolbar behaviour: the Demo toggle, Reload, and Clear finished.
/// </summary>
/// <remarks>
/// Two shipped defects motivated these:
///   1. LoadDemoData opened with <c>Sessions.Clear()</c>, which destroyed the card for a live run.
///      Because every later progress event finds its card by session id, the real pipeline then
///      became permanently invisible - not merely hidden until the next refresh.
///   2. Reload only read the persisted *history* file and skipped every entry that already had a
///      card, so by the time a user could press it there was nothing left for it to do. It never
///      asked the session manager what was running.
/// </remarks>
public class ExecutionDashboardToolbarTests
{
    private static ExecutionDashboardVM CreateVm(ExecutionSessionManager sessions)
        => new(sessions, new AgentLockManager(), new EventAggregator(),
               System.Windows.Threading.Dispatcher.CurrentDispatcher);

    private static ExecutionSessionManager NewManager() => new(new EventAggregator());

    /// <summary>Starts a real session in the manager, as a run triggered anywhere would.</summary>
    private static ExecutionSession StartRun(ExecutionSessionManager mgr, string tag)
        => mgr.BeginSession(tag, "Manual", new Dictionary<string, string>(), []);

    private static SessionCardVM Card(ExecutionDashboardVM vm, string sessionId)
        => vm.Sessions.Single(s => s.SessionId == sessionId);

    // ── Bug 1: Demo must not evict real runs ────────────────────────────────

    [Fact]
    public void Demo_Should_KeepTheRealRunVisible_When_ToggledOnAndOffDuringALiveRun()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "SP2023R2SP2 - WARM 4 Nodes Install Only");
        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        Assert.Single(vm.Sessions);

        vm.ToggleDemoCommand.Execute(null);
        Assert.True(vm.IsDemoMode);
        Assert.Contains(vm.Sessions, s => s.SessionId == run.SessionId);
        Assert.Contains(vm.Sessions, s => s.IsDemo);

        vm.ToggleDemoCommand.Execute(null);
        Assert.False(vm.IsDemoMode);
        Assert.Contains(vm.Sessions, s => s.SessionId == run.SessionId);
        Assert.DoesNotContain(vm.Sessions, s => s.IsDemo);
    }

    [Fact]
    public void Demo_Should_RemoveOnlyItsOwnCards_When_ToggledOff()
    {
        var mgr = NewManager();
        StartRun(mgr, "RealPipeline");
        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        var realIds = vm.Sessions.Select(s => s.SessionId).ToList();

        vm.ToggleDemoCommand.Execute(null);
        Assert.True(vm.Sessions.Count > realIds.Count);

        vm.ToggleDemoCommand.Execute(null);

        Assert.Equal(realIds, vm.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Demo_Should_NotCountTowardTheRealTotals_When_On()
    {
        var mgr = NewManager();
        StartRun(mgr, "RealPipeline");
        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        var activeBefore = vm.ActiveSessionCount;
        var passedBefore = vm.TotalPassedActions;

        vm.ToggleDemoCommand.Execute(null);

        Assert.Equal(activeBefore, vm.ActiveSessionCount);
        Assert.Equal(passedBefore, vm.TotalPassedActions);
    }

    [Fact]
    public void Demo_Should_NotStackDuplicateCards_When_ToggledOnTwice()
    {
        var vm = CreateVm(NewManager());

        vm.ToggleDemoCommand.Execute(null);
        var count = vm.Sessions.Count(s => s.IsDemo);

        vm.IsDemoMode = false;                 // force the ON branch a second time
        vm.ToggleDemoCommand.Execute(null);

        Assert.Equal(count, vm.Sessions.Count(s => s.IsDemo));
    }

    [Fact]
    public void Demo_Should_DropItsLogLines_When_ToggledOff()
    {
        var vm = CreateVm(NewManager());
        vm.LogEntries.Add(new LogEntryVM { SessionId = "real-1", Message = "real line" });

        vm.ToggleDemoCommand.Execute(null);
        Assert.True(vm.LogEntries.Count > 1);

        vm.ToggleDemoCommand.Execute(null);

        Assert.Single(vm.LogEntries);
        Assert.Equal("real line", vm.LogEntries[0].Message);
    }

    // ── Bug 2: Reload must actually re-query ────────────────────────────────

    [Fact]
    public void Reload_Should_ShowARunStartedElsewhere_When_TheDashboardWasAlreadyOpen()
    {
        var mgr = NewManager();
        var vm = CreateVm(mgr);
        Assert.Empty(vm.Sessions);

        // Someone triggers a pipeline from the web client / a trigger file.
        var run = StartRun(mgr, "Started.Elsewhere");

        vm.ReloadCommand.Execute(null);

        Assert.Contains(vm.Sessions, s => s.SessionId == run.SessionId);
        Assert.Equal("Running", Card(vm, run.SessionId).Status);
        Assert.Equal(1, vm.ActiveSessionCount);
    }

    [Fact]
    public void Reload_Should_CarryTheSessionDetail_When_ItAdoptsARunStartedElsewhere()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "Started.Elsewhere");
        run.UserId = "wwApps";
        run.UserDisplayName = "W W";
        run.Source = "WebClient";
        run.LockedAgents = ["warmgr", "warmpri"];

        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        var card = Card(vm, run.SessionId);
        Assert.Equal("Started.Elsewhere", card.WatchItemTag);
        Assert.Equal("wwApps", card.UserId);
        Assert.Equal("WebClient", card.Source);
        Assert.Equal("warmgr, warmpri", card.LockedAgentsList);
    }

    [Fact]
    public void Reload_Should_NotDuplicateACard_When_PressedRepeatedly()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "Started.Elsewhere");
        var vm = CreateVm(mgr);

        vm.ReloadCommand.Execute(null);
        vm.ReloadCommand.Execute(null);
        vm.ReloadCommand.Execute(null);

        Assert.Single(vm.Sessions, s => s.SessionId == run.SessionId);
    }

    [Fact]
    public void Reload_Should_KeepDemoCards_When_DemoIsOn()
    {
        var mgr = NewManager();
        var vm = CreateVm(mgr);
        vm.ToggleDemoCommand.Execute(null);
        var demoIds = vm.Sessions.Where(s => s.IsDemo).Select(s => s.SessionId).ToList();

        StartRun(mgr, "Started.Elsewhere");
        vm.ReloadCommand.Execute(null);

        Assert.True(vm.IsDemoMode);
        foreach (var id in demoIds)
            Assert.Contains(vm.Sessions, s => s.SessionId == id);
    }

    // ── Clear finished ──────────────────────────────────────────────────────

    [Fact]
    public void ClearFinished_Should_RemoveOnlyTerminalCards_And_LeaveRunningOnes()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "StillGoing");
        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        vm.Sessions.Add(new SessionCardVM { SessionId = "done-1", Status = "Success" });
        vm.Sessions.Add(new SessionCardVM { SessionId = "done-2", Status = "Failed" });
        vm.Sessions.Add(new SessionCardVM { SessionId = "done-3", Status = "Cancelled" });

        vm.ClearFinishedCommand.Execute(null);

        Assert.Single(vm.Sessions);
        Assert.Equal(run.SessionId, vm.Sessions[0].SessionId);
    }

    [Fact]
    public void ClearFinished_Should_BeDisabled_When_NothingIsFinished()
    {
        var mgr = NewManager();
        StartRun(mgr, "StillGoing");
        var vm = CreateVm(mgr);
        vm.ReloadCommand.Execute(null);

        Assert.False(vm.ClearFinishedCommand.CanExecute(null));

        vm.Sessions.Add(new SessionCardVM { SessionId = "done-1", Status = "Failed" });
        vm.RecalculateStats();

        Assert.True(vm.ClearFinishedCommand.CanExecute(null));
    }

    [Fact]
    public void ClearFinished_Should_LeaveTheSessionRecordsAlone_So_HistoryStillHasThem()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "WillFinish");
        mgr.CompleteSession(run.SessionId);

        var vm = CreateVm(mgr);
        vm.Sessions.Add(new SessionCardVM { SessionId = run.SessionId, Status = "Success" });

        vm.ClearFinishedCommand.Execute(null);

        Assert.Empty(vm.Sessions);
        Assert.NotNull(mgr.GetSession(run.SessionId));
        Assert.Contains(mgr.GetHistory(10), s => s.SessionId == run.SessionId);
    }

    [Fact]
    public void ClearFinished_Should_NotResurrectDismissedCards_When_ReloadRunsAfterwards()
    {
        var mgr = NewManager();
        var run = StartRun(mgr, "WillFinish");
        mgr.CompleteSession(run.SessionId);

        var vm = CreateVm(mgr);
        vm.Sessions.Add(new SessionCardVM { SessionId = run.SessionId, Status = "Success" });
        vm.ClearFinishedCommand.Execute(null);

        vm.ReloadCommand.Execute(null);

        Assert.DoesNotContain(vm.Sessions, s => s.SessionId == run.SessionId);
    }

    [Fact]
    public void ClearFinished_Should_NotBlockANewRun_When_ItStartsAfterTheClear()
    {
        var mgr = NewManager();
        var vm = CreateVm(mgr);
        vm.Sessions.Add(new SessionCardVM { SessionId = "done-1", Status = "Success" });
        vm.ClearFinishedCommand.Execute(null);
        Assert.Empty(vm.Sessions);

        var fresh = StartRun(mgr, "Fresh.Run");
        vm.ReloadCommand.Execute(null);

        Assert.Contains(vm.Sessions, s => s.SessionId == fresh.SessionId);
    }

    [Fact]
    public void ClearFinished_Should_ShowARelaunchedSessionAgain_When_TheSameIdGoesLive()
    {
        var mgr = NewManager();
        var vm = CreateVm(mgr);
        vm.Sessions.Add(new SessionCardVM { SessionId = "repeat-1", Status = "Failed" });
        vm.ClearFinishedCommand.Execute(null);

        // Same id comes back as a live session - dismissal must not outrank "it is running now".
        mgr.BeginSession("Repeat", "Manual", new Dictionary<string, string>(), [], sessionId: "repeat-1");
        vm.ReloadCommand.Execute(null);

        Assert.Contains(vm.Sessions, s => s.SessionId == "repeat-1");
    }

    [Fact]
    public void ClearFinished_Should_DropTheSelection_When_TheSelectedCardIsCleared()
    {
        var vm = CreateVm(NewManager());
        vm.Sessions.Add(new SessionCardVM { SessionId = "done-1", Status = "Success" });
        vm.SelectedSessionId = "done-1";

        vm.ClearFinishedCommand.Execute(null);

        Assert.Null(vm.SelectedSessionId);
    }
}
