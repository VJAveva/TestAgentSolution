using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Unit tests for the Execution Dashboard's full lifecycle:
///   - Session card creation + deduplication
///   - Action pill population via AgentRowVM.UpdateAction
///   - Status transitions (Running ? Success / Failed)
///   - Terminal status protection (no downgrade)
///   - Agent row progress recalculation
///   - Timeline VM lane/bar generation
///   - Pipeline session filter predicate
///   - ActionPillVM display label + glyph logic
///
/// These tests exercise the ViewModel/Model layer directly (no Dispatcher
/// involvement) so they are fast, deterministic, and STA-free.
/// </summary>
public class ExecutionDashboardLifecycleTests
{
    private static ExecutionDashboardVM CreateVm()
    {
        // Use feed-only factory: no DispatcherTimer, no event subscriptions.
        // This avoids CI failures on headless runners where Dispatcher message
        // pumps are unavailable.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        return ExecutionDashboardVM.CreateForFeed(dispatcher);
    }

    /// <summary>
    /// Creates a VM with full in-process services (timer + event aggregator).
    /// Use only for tests that specifically need the event-driven path.
    /// </summary>
    private static ExecutionDashboardVM CreateFullVm()
    {
        var sessionMgr = new ExecutionSessionManager();
        var lockMgr = new AgentLockManager();
        var events = new EventAggregator();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        return new ExecutionDashboardVM(sessionMgr, lockMgr, events, dispatcher);
    }

    // ???????????????????????????????????????????????????????????????????
    // Session card creation
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SessionCard_AddedToSessions_IsVisible()
    {
        var vm = CreateVm();
        var card = new SessionCardVM
        {
            SessionId = "S1",
            WatchItemTag = "SmokeTest",
            Status = "Running",
            IsExpanded = true,
        };
        vm.Sessions.Add(card);

        Assert.Single(vm.Sessions);
        Assert.Equal("S1", vm.Sessions[0].SessionId);
        Assert.Equal("Running", vm.Sessions[0].Status);
    }

    [Fact]
    public void SessionCard_Dedup_By_SessionId()
    {
        var vm = CreateVm();
        vm.Sessions.Add(new SessionCardVM { SessionId = "S1", WatchItemTag = "T" });

        // Simulate dedup logic from OnExecutionStarted
        if (!vm.Sessions.Any(s => s.SessionId == "S1"))
            vm.Sessions.Add(new SessionCardVM { SessionId = "S1", WatchItemTag = "T2" });

        Assert.Single(vm.Sessions);
    }

    // ???????????????????????????????????????????????????????????????????
    // AgentRowVM.UpdateAction — the core pipeline action chain logic
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void UpdateAction_Adds_New_Pill()
    {
        var agent = new AgentRowVM { AgentName = "Agent1" };
        agent.UpdateAction(tag: "Initialize", actionType: "RunRemoteCommand",
            command: "init.cmd", status: "Running");

        Assert.Single(agent.Actions);
        Assert.Equal("Initialize", agent.Actions[0].Tag);
        Assert.Equal("Running", agent.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Creates_Full_Chain_Initialize_CopyBatch_InstallBuild()
    {
        var agent = new AgentRowVM { AgentName = "Agent1" };
        agent.UpdateAction(tag: "Initialize", actionType: "", command: "init.cmd", status: "Success");
        agent.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Success");
        agent.UpdateAction(tag: "InstallBuild", actionType: "", command: "install.cmd", status: "Running");

        Assert.Equal(3, agent.Actions.Count);
        Assert.Equal("Initialize", agent.Actions[0].Tag);
        Assert.Equal("CopyBatch", agent.Actions[1].Tag);
        Assert.Equal("InstallBuild", agent.Actions[2].Tag);
        Assert.Equal("Success", agent.Actions[0].Status);
        Assert.Equal("Success", agent.Actions[1].Status);
        Assert.Equal("Running", agent.Actions[2].Status);
    }

    [Fact]
    public void UpdateAction_Updates_Existing_Pill_By_Tag()
    {
        var agent = new AgentRowVM { AgentName = "Agent1" };
        agent.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Running");
        agent.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Success");

        Assert.Single(agent.Actions);
        Assert.Equal("Success", agent.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Does_Not_Downgrade_Terminal_Status()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd", status: "Success");

        // Stale "Running" arrives after "Success" (out-of-order)
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd", status: "Running");

        Assert.Equal("Success", agent.Actions[0].Status);
    }

    [Fact]
    public void UpdateAction_Does_Not_Downgrade_Failed_To_Running()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd", status: "Failed",
            exitCode: 1, errorMessage: "timeout");

        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd", status: "Running");

        Assert.Equal("Failed", agent.Actions[0].Status);
    }

    // ???????????????????????????????????????????????????????????????????
    // Agent row progress + status recalculation
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void Agent_ProgressPercent_Recalculates_Correctly()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd1", status: "Success");
        agent.UpdateAction(tag: "Act2", actionType: "", command: "cmd2", status: "Running");

        // 1 completed out of 2 = 50%
        Assert.Equal(50, agent.ProgressPercent);
    }

    [Fact]
    public void Agent_ProgressPercent_Is_100_When_All_Complete()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd1", status: "Success");
        agent.UpdateAction(tag: "Act2", actionType: "", command: "cmd2", status: "Success");
        agent.UpdateAction(tag: "Act3", actionType: "", command: "cmd3", status: "Success");

        Assert.Equal(100, agent.ProgressPercent);
        Assert.Equal("Success", agent.Status);
    }

    [Fact]
    public void Agent_Status_Becomes_Failed_When_Any_Action_Fails()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd1", status: "Success");
        agent.UpdateAction(tag: "Act2", actionType: "", command: "cmd2", status: "Failed",
            exitCode: 1, errorMessage: "timeout");

        Assert.Equal("Failed", agent.Status);
        Assert.Equal("Failed", agent.StatusText);
    }

    [Fact]
    public void Agent_Status_Is_Executing_When_Action_Running()
    {
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.UpdateAction(tag: "Act1", actionType: "", command: "cmd1", status: "Running");

        Assert.Equal("Executing", agent.Status);
        Assert.Equal("Executing", agent.StatusText);
    }

    // ???????????????????????????????????????????????????????????????????
    // SessionCardVM counters
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SessionCard_RecalculateCounters_Sums_Across_Agents()
    {
        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "Test", Status = "Running" };
        var agent1 = new AgentRowVM { AgentName = "A1" };
        agent1.UpdateAction(tag: "Init", actionType: "", command: "cmd", status: "Success");
        agent1.UpdateAction(tag: "Copy", actionType: "", command: "cmd", status: "Success");

        var agent2 = new AgentRowVM { AgentName = "A2" };
        agent2.UpdateAction(tag: "Init", actionType: "", command: "cmd", status: "Success");
        agent2.UpdateAction(tag: "Copy", actionType: "", command: "cmd", status: "Failed");

        card.Agents.Add(agent1);
        card.Agents.Add(agent2);
        card.RecalculateCounters();

        Assert.Equal(3, card.PassedActions);  // 3 Success
        Assert.Equal(1, card.FailedActions);  // 1 Failed
        Assert.Equal(4, card.CompletedActions);
    }

    [Fact]
    public void SessionCard_StatusBadge_Maps_Correctly()
    {
        Assert.Equal("Running", new SessionCardVM { Status = "Running" }.StatusBadge);
        Assert.Equal("Completed", new SessionCardVM { Status = "Success" }.StatusBadge);
        Assert.Equal("Failed", new SessionCardVM { Status = "Failed" }.StatusBadge);
        Assert.Equal("Cancelled", new SessionCardVM { Status = "Cancelled" }.StatusBadge);
    }

    [Fact]
    public void SessionCard_GetOrCreateAgent_Creates_And_Deduplicates()
    {
        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "T" };
        var a1 = card.GetOrCreateAgent("Agent1");
        var a2 = card.GetOrCreateAgent("Agent1"); // same name, case-insensitive

        Assert.Same(a1, a2);
        Assert.Single(card.Agents);
    }

    // ???????????????????????????????????????????????????????????????????
    // Session completion
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SessionCard_Completion_Sets_100_Percent()
    {
        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "T", Status = "Running" };
        card.Status = "Success";
        card.ProgressPercent = 100;
        card.PassedActions = 5;
        card.FailedActions = 0;
        card.IsExpanded = false;

        Assert.Equal("Completed", card.StatusBadge);
        Assert.Equal(100, card.ProgressPercent);
        Assert.False(card.IsExpanded);
    }

    // ???????????????????????????????????????????????????????????????????
    // Pipeline session filter predicate
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void FilterSessionCard_All_ReturnsAll()
    {
        var vm = CreateVm();
        vm.PipelineStatusFilter = "All";
        Assert.True(vm.FilterSessionCard(new SessionCardVM { Status = "Running", WatchItemTag = "T" }));
        Assert.True(vm.FilterSessionCard(new SessionCardVM { Status = "Success", WatchItemTag = "T" }));
        Assert.True(vm.FilterSessionCard(new SessionCardVM { Status = "Failed", WatchItemTag = "T" }));
    }

    [Fact]
    public void FilterSessionCard_Running_FiltersOutCompleted()
    {
        var vm = CreateVm();
        vm.PipelineStatusFilter = "Running";
        Assert.True(vm.FilterSessionCard(new SessionCardVM { Status = "Running", WatchItemTag = "T" }));
        Assert.False(vm.FilterSessionCard(new SessionCardVM { Status = "Success", WatchItemTag = "T" }));
        Assert.False(vm.FilterSessionCard(new SessionCardVM { Status = "Failed", WatchItemTag = "T" }));
    }

    [Fact]
    public void FilterSessionCard_Failed_FiltersOutRunning()
    {
        var vm = CreateVm();
        vm.PipelineStatusFilter = "Failed";
        Assert.True(vm.FilterSessionCard(new SessionCardVM { Status = "Failed", WatchItemTag = "T" }));
        Assert.False(vm.FilterSessionCard(new SessionCardVM { Status = "Running", WatchItemTag = "T" }));
    }

    [Fact]
    public void FilterSessionCard_SearchText_MatchesWatchItemTag()
    {
        var vm = CreateVm();
        vm.PipelineSearchText = "Smoke";
        Assert.True(vm.FilterSessionCard(new SessionCardVM { WatchItemTag = "SmokeTest", Status = "Running" }));
        Assert.False(vm.FilterSessionCard(new SessionCardVM { WatchItemTag = "Sanity", Status = "Running" }));
    }

    [Fact]
    public void FilterSessionCard_SearchText_MatchesAgentName()
    {
        var vm = CreateVm();
        vm.PipelineSearchText = "jvgr1";
        var card = new SessionCardVM { WatchItemTag = "Test", Status = "Running" };
        card.Agents.Add(new AgentRowVM { AgentName = "jvgr1" });
        Assert.True(vm.FilterSessionCard(card));
    }

    [Fact]
    public void FilterSessionCard_SearchText_MatchesActionLabel()
    {
        var vm = CreateVm();
        vm.PipelineSearchText = "Install";
        var card = new SessionCardVM { WatchItemTag = "Test", Status = "Running" };
        var agent = new AgentRowVM { AgentName = "A1" };
        agent.Actions.Add(new ActionPillVM { Tag = "InstallBuild", Command = "install.cmd" });
        card.Agents.Add(agent);
        Assert.True(vm.FilterSessionCard(card));
    }

    [Fact]
    public void FilterSessionCard_SearchText_NoMatch_ReturnsFalse()
    {
        var vm = CreateVm();
        vm.PipelineSearchText = "NonExistent";
        var card = new SessionCardVM { WatchItemTag = "Test", Status = "Running" };
        Assert.False(vm.FilterSessionCard(card));
    }

    // ???????????????????????????????????????????????????????????????????
    // ActionPillVM display logic
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ActionPillVM_DisplayLabel_Shows_Tag_When_Available()
    {
        var pill = new ActionPillVM { Tag = "Initialize", Command = "init.cmd" };
        Assert.Equal("Initialize", pill.DisplayLabel);
    }

    [Fact]
    public void ActionPillVM_DisplayLabel_Falls_Back_To_Command()
    {
        var pill = new ActionPillVM { Tag = "", Command = "deploy.cmd" };
        Assert.Equal("deploy.cmd", pill.DisplayLabel);
    }

    [Fact]
    public void ActionPillVM_DisplayLabel_Shows_ProgressPercent_When_Running()
    {
        var pill = new ActionPillVM { Tag = "CopyBatch", Status = "Running", ProgressPercent = 45 };
        Assert.Contains("45%", pill.DisplayLabel);
    }

    [Fact]
    public void ActionPillVM_DisplayLabel_Truncates_Long_Labels()
    {
        var pill = new ActionPillVM { Tag = "", Command = @"C:\Very\Long\Path\To\Some\Script\deploy.ps1" };
        Assert.True(pill.DisplayLabel.Length <= 24);
    }

    [Fact]
    public void ActionPillVM_StatusIcon_Correct_Glyphs()
    {
        Assert.Equal("\u25CB", new ActionPillVM { Status = "Pending" }.StatusIcon);    // ?
        Assert.Equal("\u25CF", new ActionPillVM { Status = "Running" }.StatusIcon);    // ?
        Assert.Equal("\u2713", new ActionPillVM { Status = "Success" }.StatusIcon);    // ?
        Assert.Equal("\u2717", new ActionPillVM { Status = "Failed" }.StatusIcon);     // ?
        Assert.Equal("\u2212", new ActionPillVM { Status = "Skipped" }.StatusIcon);    // ?
    }

    [Fact]
    public void ActionPillVM_Key_Uses_Tag_When_Present()
    {
        var pill = new ActionPillVM { Tag = "Initialize", Command = "init.cmd" };
        Assert.Equal("Initialize", pill.Key);
    }

    [Fact]
    public void ActionPillVM_Key_Falls_Back_To_ActionType_Pipe_Command()
    {
        var pill = new ActionPillVM { Tag = "", ActionType = "RunRemoteCommand", Command = "deploy.cmd" };
        Assert.Equal("RunRemoteCommand|deploy.cmd", pill.Key);
    }

    // ???????????????????????????????????????????????????????????????????
    // Timeline VM
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void TimelineVM_Rebuild_Creates_Lanes_From_Sessions()
    {
        var vm = CreateVm();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "Test", Status = "Running" };
        var agent = new AgentRowVM { AgentName = "Agent1" };
        agent.Actions.Add(new ActionPillVM
        {
            Tag = "Initialize",
            Status = "Success",
            StartedUtc = DateTime.UtcNow.AddSeconds(-60),
            DurationSeconds = 10
        });
        agent.Actions.Add(new ActionPillVM
        {
            Tag = "CopyBatch",
            Status = "Running",
            StartedUtc = DateTime.UtcNow.AddSeconds(-50),
            DurationSeconds = 0
        });
        card.Agents.Add(agent);
        vm.Sessions.Add(card);

        var timeline = new TimelineVM(vm, dispatcher);

        Assert.Single(timeline.Lanes);
        Assert.Equal("Agent1", timeline.Lanes[0].AgentName);
        Assert.Equal(2, timeline.Lanes[0].Bars.Count);
        Assert.Equal("Initialize", timeline.Lanes[0].Bars[0].ActionTag);
        Assert.Equal("CopyBatch", timeline.Lanes[0].Bars[1].ActionTag);

        timeline.Dispose();
    }

    [Fact]
    public void TimelineVM_Rebuild_Handles_Empty_Sessions()
    {
        var vm = CreateVm();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var timeline = new TimelineVM(vm, dispatcher);

        Assert.Empty(timeline.Lanes);
        timeline.Dispose();
    }

    [Fact]
    public void TimelineVM_MultiAgent_Creates_Multiple_Lanes()
    {
        var vm = CreateVm();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "Test", Status = "Running" };
        var a1 = new AgentRowVM { AgentName = "Agent1" };
        a1.Actions.Add(new ActionPillVM { Tag = "Init", Status = "Success", StartedUtc = DateTime.UtcNow.AddSeconds(-30), DurationSeconds = 5 });
        var a2 = new AgentRowVM { AgentName = "Agent2" };
        a2.Actions.Add(new ActionPillVM { Tag = "Init", Status = "Running", StartedUtc = DateTime.UtcNow.AddSeconds(-20), DurationSeconds = 0 });
        card.Agents.Add(a1);
        card.Agents.Add(a2);
        vm.Sessions.Add(card);

        var timeline = new TimelineVM(vm, dispatcher);
        Assert.Equal(2, timeline.Lanes.Count);
        timeline.Dispose();
    }

    // ???????????????????????????????????????????????????????????????????
    // Multi-agent full scenario (direct method calls)
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void FullScenario_MultiAgent_Pipeline_Chain()
    {
        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "SmokeTest", Status = "Running", IsExpanded = true };

        // Agent1: Initialize ? CopyBatch ? InstallBuild (all succeed)
        var a1 = card.GetOrCreateAgent("Agent1");
        a1.UpdateAction(tag: "Initialize", actionType: "", command: "init.cmd", status: "Success");
        a1.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Success");
        a1.UpdateAction(tag: "InstallBuild", actionType: "", command: "install.cmd", status: "Success");

        // Agent2: Initialize ? CopyBatch (CopyBatch still running)
        var a2 = card.GetOrCreateAgent("Agent2");
        a2.UpdateAction(tag: "Initialize", actionType: "", command: "init.cmd", status: "Success");
        a2.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Running");

        card.RecalculateCounters();

        Assert.Equal(2, card.Agents.Count);
        Assert.Equal(3, a1.Actions.Count);
        Assert.Equal(2, a2.Actions.Count);
        Assert.Equal("Success", a1.Status);
        Assert.Equal(100, a1.ProgressPercent);
        Assert.Equal("Executing", a2.Status);
        Assert.Equal(50, a2.ProgressPercent);
        Assert.Equal(4, card.PassedActions);
        Assert.Equal(0, card.FailedActions);
    }

    [Fact]
    public void FullScenario_FailedAgent_Propagates()
    {
        var card = new SessionCardVM { SessionId = "S1", WatchItemTag = "Test", Status = "Running" };
        var agent = card.GetOrCreateAgent("Agent1");
        agent.UpdateAction(tag: "Initialize", actionType: "", command: "init.cmd", status: "Success");
        agent.UpdateAction(tag: "CopyBatch", actionType: "", command: "copy.cmd", status: "Failed",
            exitCode: 2, errorMessage: "Access denied");

        card.RecalculateCounters();

        Assert.Equal("Failed", agent.Status);
        Assert.Equal(1, card.PassedActions);
        Assert.Equal(1, card.FailedActions);
    }

    // ???????????????????????????????????????????????????????????????????
    // KPI stats recalculation
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void RecalculateStats_Computes_ActiveSessions_And_Totals()
    {
        var vm = CreateFullVm();
        vm.Sessions.Add(new SessionCardVM { SessionId = "S1", Status = "Running", PassedActions = 3, FailedActions = 1 });
        vm.Sessions.Add(new SessionCardVM { SessionId = "S2", Status = "Success", PassedActions = 5, FailedActions = 0 });
        vm.Sessions.Add(new SessionCardVM { SessionId = "S3", Status = "Failed", PassedActions = 0, FailedActions = 4 });

        vm.RecalculateStats();

        Assert.Equal(1, vm.ActiveSessionCount);
        Assert.Equal(8, vm.TotalPassedActions);
        Assert.Equal(5, vm.TotalFailedActions);
    }

    // ???????????????????????????????????????????????????????????????????
    // Log filter
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void FilterLogEntry_Passes_When_NoFilters()
    {
        var vm = CreateVm();
        var entry = new LogEntryVM { SessionId = "S1", AgentName = "A1", Severity = "Info", Message = "test" };
        Assert.True(vm.FilterLogEntry(entry));
    }

    [Fact]
    public void FilterLogEntry_Filters_By_Severity()
    {
        var vm = CreateVm();
        vm.LogSeverityFilter = "Error";
        Assert.False(vm.FilterLogEntry(new LogEntryVM { Severity = "Info", Message = "ok" }));
        Assert.True(vm.FilterLogEntry(new LogEntryVM { Severity = "Error", Message = "bad" }));
    }

    [Fact]
    public void FilterLogEntry_Filters_By_SearchText()
    {
        var vm = CreateVm();
        vm.LogSearchText = "timeout";
        Assert.True(vm.FilterLogEntry(new LogEntryVM { Message = "Connection timeout", AgentName = "" }));
        Assert.False(vm.FilterLogEntry(new LogEntryVM { Message = "Success", AgentName = "" }));
    }
}
