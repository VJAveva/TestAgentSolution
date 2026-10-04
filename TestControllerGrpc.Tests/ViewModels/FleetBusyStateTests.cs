using System.Windows.Threading;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The fleet panel used to decide "busy" purely from <see cref="AgentLockManager"/>. When a pipeline took
/// no agent locks — which is what a Ref-only WatchList produced — all nine machines rendered Free / "Idle"
/// with Busy 0 while they were executing. Busy must therefore also follow the controller's own dispatch
/// state, which is independent of both the lock and the agent's self-report.
/// </summary>
public class FleetBusyStateTests
{
    private const string Agent = "jvkpri";

    private static Mock<IAgentGrpcDispatcher> AgentsNamed(bool isExecuting = false, params string[] agents)
    {
        var names = agents.Length > 0 ? agents : [Agent];
        var d = new Mock<IAgentGrpcDispatcher>();
        d.Setup(x => x.RegisteredAgents).Returns(names);
        d.Setup(x => x.GetAgentAddress(It.IsAny<string>())).Returns("http://host:5200");
        d.Setup(x => x.GetAllAgentHealth()).Returns(
            names.ToDictionary(n => n, n => new AgentHealthState { AgentName = n }, StringComparer.OrdinalIgnoreCase));
        d.Setup(x => x.GetAgentHealth(It.IsAny<string>())).Returns((string n) => new AgentHealthState { AgentName = n });
        d.Setup(x => x.IsAgentExecuting(It.IsAny<string>())).Returns(isExecuting);
        return d;
    }

    private static FleetVM Build(
        Mock<IAgentGrpcDispatcher> dispatcher, AgentLockManager locks, ExecutionSessionManager sessions)
        => new(dispatcher.Object, locks, sessions, new EventAggregator(), Dispatcher.CurrentDispatcher);

    private static ExecutionSession StartSession(ExecutionSessionManager sessions, string tag = "SP2026 - Sanity")
    {
        var session = sessions.BeginSession(tag, "Renamed", [], [], "sess-1");
        session.UserDisplayName = "Vinod";
        session.UserRole = "Admin";
        return session;
    }

    private static NodeProgressEvent Progress(string status, string sessionId = "sess-1", string agent = Agent)
        => new(sessionId, agent, "Run Prepare-Agent", "RunRemoteCommand", "Prepare-Agent.bat", status);

    [Fact]
    public void Refresh_Should_ReportBusy_When_ControllerIsDispatchingWithoutAnAgentLock()
    {
        var vm = Build(AgentsNamed(isExecuting: true), new AgentLockManager(), new ExecutionSessionManager());

        vm.Refresh();

        Assert.Equal(1, vm.BusyCount);
        Assert.Equal(0, vm.FreeCount);
        Assert.Equal("Busy", vm.Cards.Single().Status);
    }

    [Fact]
    public void Refresh_Should_ReportBusy_When_ANodeProgressEventNamesTheAgentButNoLockExists()
    {
        var sessions = new ExecutionSessionManager();
        StartSession(sessions);
        var vm = Build(AgentsNamed(), new AgentLockManager(), sessions);

        vm.OnNodeProgress(Progress("Running"));
        vm.Refresh();

        var card = vm.Cards.Single();
        Assert.Equal(1, vm.BusyCount);
        Assert.Equal("Busy", card.Status);
        Assert.Equal("SP2026 - Sanity", card.WatchItemTag);
        Assert.Equal("Vinod", card.Owner);
    }

    [Fact]
    public void Refresh_Should_ReportFree_When_TheActionReportedATerminalStatusAndNothingElseClaimsTheAgent()
    {
        var sessions = new ExecutionSessionManager();
        var vm = Build(AgentsNamed(), new AgentLockManager(), sessions);

        vm.OnNodeProgress(Progress("Running"));
        vm.OnNodeProgress(Progress("Success"));
        vm.Refresh();

        Assert.Equal(0, vm.BusyCount);
        Assert.Equal("Free", vm.Cards.Single().Status);
    }

    [Fact]
    public void Refresh_Should_ReleaseTheAgent_When_TheRunEndedWithoutATerminalNodeEvent()
    {
        // A cancelled or crashed action never reports a terminal status, so the dispatch entry has to be
        // reconciled against the live session list or the card stays Busy forever.
        var sessions = new ExecutionSessionManager();
        StartSession(sessions);
        var vm = Build(AgentsNamed(), new AgentLockManager(), sessions);
        vm.OnNodeProgress(Progress("Running"));

        sessions.CompleteSession("sess-1");
        vm.Refresh();

        Assert.Equal(0, vm.BusyCount);
        Assert.Equal("Free", vm.Cards.Single().Status);
    }

    [Fact]
    public void Refresh_Should_CountTheAgentOnce_When_BothTheLockAndDispatchStateClaimIt()
    {
        var sessions = new ExecutionSessionManager();
        StartSession(sessions);
        var locks = new AgentLockManager();
        locks.TryLockAgents([Agent], "sess-1", "SP2026 - Sanity", "WPF/vinod@jvgr22", "WPF");
        var vm = Build(AgentsNamed(isExecuting: true), locks, sessions);

        vm.OnNodeProgress(Progress("Running"));
        vm.Refresh();

        Assert.Equal(1, vm.BusyCount);
        Assert.Equal(0, vm.FreeCount);
        Assert.Equal("SP2026 - Sanity", vm.Cards.Single().WatchItemTag);
    }

    [Fact]
    public void Refresh_Should_NotClaimAnAgent_When_TheRunningNodeIsAControllerLocalAction()
    {
        var sessions = new ExecutionSessionManager();
        StartSession(sessions);
        var vm = Build(AgentsNamed(), new AgentLockManager(), sessions);

        vm.OnNodeProgress(Progress("Running", agent: "Controller"));
        vm.Refresh();

        Assert.Equal(0, vm.BusyCount);
        Assert.Equal("Free", vm.Cards.Single().Status);
    }

    [Fact]
    public void Refresh_Should_LeaveOtherAgentsFree_When_OnlyOneIsExecuting()
    {
        var sessions = new ExecutionSessionManager();
        StartSession(sessions);
        var dispatcher = AgentsNamed(agents: [Agent, "warmpri", "jvgr1"]);
        var vm = Build(dispatcher, new AgentLockManager(), sessions);

        vm.OnNodeProgress(Progress("Running"));
        vm.Refresh();

        Assert.Equal(1, vm.BusyCount);
        Assert.Equal(2, vm.FreeCount);
    }
}
