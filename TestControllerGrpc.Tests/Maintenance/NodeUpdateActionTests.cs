using System.Windows.Threading;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// The update button is one control with two verbs, so the cases that matter are the transitions: an agent
/// that cannot install, a search that finds nothing, and state surviving the row rebuild that happens on
/// every status report.
/// </summary>
public class NodeUpdateActionTests
{
    private const string Node = "JVGR2";

    private static FleetUpdatesVM Build(Mock<INodeUpdateInstaller> installer, params string[] agents)
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.SetupGet(d => d.RegisteredAgents).Returns(agents.Length == 0 ? [Node] : agents);

        // RebuildRows bails out without a store, so rows would never be built.
        var store = new Mock<INodeUpdateStatusStore>();
        store.Setup(s => s.GetAll()).Returns([]);

        return new FleetUpdatesVM(
            dispatcher.Object,
            Dispatcher.CurrentDispatcher,
            store: store.Object,
            installer: installer.Object);
    }

    private static Mock<INodeUpdateInstaller> Installer(
        bool supported = true, UpdateSearchResult? search = null, UpdateInstallResult? install = null)
    {
        var m = new Mock<INodeUpdateInstaller>();
        m.Setup(i => i.IsInstallSupportedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(supported);
        m.Setup(i => i.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(search ?? UpdateSearchResult.None);
        m.Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(install ?? new UpdateInstallResult(true, 0, 0, false));
        return m;
    }

    [Fact]
    public void Action_Should_StartIdleAndOfferCheck_When_NothingHasHappened()
    {
        var action = Build(Installer()).ActionFor(Node);

        Assert.Equal(NodeUpdateActionState.Idle, action.State);
        Assert.Equal("Check for updates", action.Label);
        Assert.True(action.CanAct);
    }

    [Fact]
    public async Task Check_Should_ReportUpToDate_When_NoUpdatesAreFound()
    {
        var vm = Build(Installer(search: UpdateSearchResult.None));

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var action = vm.ActionFor(Node);
        Assert.Equal(NodeUpdateActionState.Done, action.State);
        Assert.Equal("Up to date", action.Label);
    }

    [Fact]
    public async Task Check_Should_BecomeAnInstallAction_When_UpdatesAreFound()
    {
        var vm = Build(Installer(search: new UpdateSearchResult(true, 3, ["A", "B", "C"])));

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var action = vm.ActionFor(Node);
        Assert.Equal(NodeUpdateActionState.Available, action.State);
        Assert.Equal("Install 3 updates", action.Label);
    }

    [Fact]
    public async Task Check_Should_Singularise_When_ExactlyOneUpdateIsFound()
    {
        var vm = Build(Installer(search: new UpdateSearchResult(true, 1, ["A"])));

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        Assert.Equal("Install 1 update", vm.ActionFor(Node).Label);
    }

    [Fact]
    public async Task Check_Should_DisableTheButton_When_AgentCannotInstall()
    {
        // Agents provisioned under an auto-logon user are not elevated; a silent no-op would be worse.
        var vm = Build(Installer(supported: false));

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var action = vm.ActionFor(Node);
        Assert.Equal(NodeUpdateActionState.Unsupported, action.State);
        Assert.False(action.CanAct);
        Assert.Contains("elevated", action.Detail);
    }

    [Fact]
    public async Task SecondClick_Should_Install_When_UpdatesAreAvailable()
    {
        var installer = Installer(
            search: new UpdateSearchResult(true, 2, ["A", "B"]),
            install: new UpdateInstallResult(true, 2, 0, RebootRequired: true));
        var vm = Build(installer);

        await vm.UpdateActionCommand.ExecuteAsync(Node);   // check
        await vm.UpdateActionCommand.ExecuteAsync(Node);   // install

        var action = vm.ActionFor(Node);
        installer.Verify(i => i.InstallAsync(Node, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(NodeUpdateActionState.Done, action.State);
        Assert.True(action.RebootRequired);
        Assert.Contains("reboot is required", action.Detail);
    }

    [Fact]
    public async Task Install_Should_ShowRetryableError_When_InstallFails()
    {
        var vm = Build(Installer(
            search: new UpdateSearchResult(true, 1, ["A"]),
            install: UpdateInstallResult.Failed("2 update(s) failed to install.")));

        await vm.UpdateActionCommand.ExecuteAsync(Node);
        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var action = vm.ActionFor(Node);
        Assert.Equal(NodeUpdateActionState.Error, action.State);
        Assert.True(action.CanAct);                        // the operator can retry
        Assert.Contains("failed to install", action.Detail);
    }

    [Fact]
    public async Task Check_Should_ShowError_When_TheInstallerThrows()
    {
        var installer = Installer();
        installer.Setup(i => i.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel is dead"));
        var vm = Build(installer);

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var action = vm.ActionFor(Node);
        Assert.Equal(NodeUpdateActionState.Error, action.State);
        Assert.Contains("channel is dead", action.Detail);
    }

    [Fact]
    public async Task ActionState_Should_SurviveARowRebuild_When_AnotherNodeReports()
    {
        // RebuildRows throws every row away. If the button state lived on the row, an unrelated status
        // report mid-install would silently reset it to "Check for updates".
        var vm = Build(Installer(search: new UpdateSearchResult(true, 4, ["A"])));
        await vm.UpdateActionCommand.ExecuteAsync(Node);

        var before = vm.ActionFor(Node);
        vm.RefreshCommand.Execute(null);

        Assert.Same(before, vm.ActionFor(Node));
        Assert.Equal(NodeUpdateActionState.Available, vm.ActionFor(Node).State);
        Assert.Equal("Install 4 updates", vm.ActionFor(Node).Label);
    }

    [Fact]
    public async Task Action_Should_BeSharedWithTheGridRow_When_RowsAreRebuilt()
    {
        var vm = Build(Installer(search: new UpdateSearchResult(true, 2, ["A"])), Node);
        await vm.UpdateActionCommand.ExecuteAsync(Node);

        vm.RefreshCommand.Execute(null);

        var row = Assert.Single(vm.Rows, r => r.AgentName == Node);
        Assert.Same(vm.ActionFor(Node), row.Action);
        Assert.Equal("Install 2 updates", row.Action.Label);
    }

    [Fact]
    public async Task Action_Should_BeUnsupported_When_NoInstallerIsConfigured()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.SetupGet(d => d.RegisteredAgents).Returns([Node]);
        var vm = new FleetUpdatesVM(dispatcher.Object, Dispatcher.CurrentDispatcher);

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        Assert.Equal(NodeUpdateActionState.Unsupported, vm.ActionFor(Node).State);
    }
}
