using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// P2-1 validation: <see cref="ExecutionDashboardVM.FilterLogEntry"/> must
///   - filter strictly when an agent is selected and the entry has an agent,
///   - keep session-scoped lines (no agent) visible under any selection,
///   - filter by SessionId, severity, and free-text search.
///
/// We construct the VM via reflection-free DI: the filter has no dependency
/// on the timer/event subscriptions, so a minimal stub graph is sufficient.
/// </summary>
public class ExecutionDashboardFilterTests
{
    private static ExecutionDashboardVM CreateVm()
    {
        // FilterLogEntry only reads Selected* / LogSeverityFilter / LogSearchText
        // from the VM, so the heavy collaborators can be left in their default
        // states; the dispatcher must be the current thread's dispatcher so the
        // DispatcherTimer can latch onto something during construction.
        var sessionMgr = new TestControllerGrpc.Services.ExecutionSessionManager();
        var lockMgr = new TestControllerGrpc.Services.AgentLockManager();
        var events = new TestControllerGrpc.Services.EventAggregator();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        return new ExecutionDashboardVM(sessionMgr, lockMgr, events, dispatcher);
    }

    private static LogEntryVM Entry(
        string sessionId = "S1",
        string agent = "",
        string severity = "Info",
        string message = "hello",
        string category = "Pipeline") => new()
        {
            SessionId = sessionId,
            AgentName = agent,
            Severity = severity,
            Message = message,
            Category = category,
        };

    [Fact]
    public void FilterLogEntry_Should_KeepEntry_When_NoFiltersActive()
    {
        var vm = CreateVm();
        Assert.True(vm.FilterLogEntry(Entry()));
    }

    [Fact]
    public void FilterLogEntry_Should_DropEntry_When_DifferentSessionSelected()
    {
        var vm = CreateVm();
        vm.SelectedSessionId = "OTHER";
        Assert.False(vm.FilterLogEntry(Entry(sessionId: "S1")));
    }

    [Fact]
    public void FilterLogEntry_Should_DropEntry_When_DifferentAgentSelected()
    {
        var vm = CreateVm();
        vm.SelectedAgentName = "AgentA";
        Assert.False(vm.FilterLogEntry(Entry(agent: "AgentB")));
    }

    [Fact]
    public void FilterLogEntry_Should_KeepEntry_When_MatchingAgentSelected()
    {
        var vm = CreateVm();
        vm.SelectedAgentName = "AgentA";
        Assert.True(vm.FilterLogEntry(Entry(agent: "AgentA")));
    }

    [Fact]
    public void FilterLogEntry_Should_KeepSessionScopedLine_When_AgentSelected()
    {
        // P2-1 contract: empty AgentName == session-scope; must remain visible
        // under any agent selection so the user still sees lifecycle messages.
        var vm = CreateVm();
        vm.SelectedAgentName = "AgentA";
        Assert.True(vm.FilterLogEntry(Entry(agent: "")));
    }

    [Fact]
    public void FilterLogEntry_Should_DropEntry_When_SeverityFilterDoesNotMatch()
    {
        var vm = CreateVm();
        vm.LogSeverityFilter = "Error";
        Assert.False(vm.FilterLogEntry(Entry(severity: "Info")));
    }

    [Fact]
    public void FilterLogEntry_Should_KeepEntry_When_SearchTextMatchesMessage()
    {
        var vm = CreateVm();
        vm.LogSearchText = "Hello";
        Assert.True(vm.FilterLogEntry(Entry(message: "hello world")));
    }

    // ?? P2-2: KPI counters span Running + Completed cards ???????????????

    private static SessionCardVM Card(string id, string status, int passed, int failed) => new()
    {
        SessionId = id,
        WatchItemTag = id,
        Status = status,
        PassedActions = passed,
        FailedActions = failed,
    };

    [Fact]
    public void RecalculateStats_Should_SumPassedAndFailed_AcrossRunningAndCompletedCards()
    {
        var vm = CreateVm();
        vm.Sessions.Add(Card("S1", "Running", passed: 3, failed: 1));
        vm.Sessions.Add(Card("S2", "Completed", passed: 5, failed: 2));
        vm.Sessions.Add(Card("S3", "Failed", passed: 0, failed: 4));

        vm.RecalculateStats();

        // P2-2 contract: completed cards still contribute to the KPI strip.
        Assert.Equal(8, vm.TotalPassedActions);   // 3 + 5 + 0
        Assert.Equal(7, vm.TotalFailedActions);   // 1 + 2 + 4
        Assert.Equal(1, vm.ActiveSessionCount);   // Running-only
    }

    [Fact]
    public void RecalculateStats_Should_RetainTotals_When_NoRunningSessions()
    {
        var vm = CreateVm();
        vm.Sessions.Add(Card("S1", "Completed", passed: 4, failed: 1));
        vm.Sessions.Add(Card("S2", "Failed",    passed: 0, failed: 3));

        vm.RecalculateStats();

        // Pre-P2-2 this snapped to 0 / 0 the moment all sessions completed.
        Assert.Equal(4, vm.TotalPassedActions);
        Assert.Equal(4, vm.TotalFailedActions);
        Assert.Equal(0, vm.ActiveSessionCount);
        Assert.Equal(0, vm.OverallProgressPercent);   // averaging only Running
    }
}
