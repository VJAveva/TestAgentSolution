using System.Windows.Threading;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// IT owns patching, so the tool reports posture and does not install. Checking stays open - parking the
/// install verb must not take the reporting half of the button with it.
/// </summary>
public class UpdateInstallParkedTests
{
    private const string Node = "JVGR2";

    private static Mock<INodeUpdateInstaller> Installer(int available)
    {
        var m = new Mock<INodeUpdateInstaller>();
        m.Setup(i => i.IsInstallSupportedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        m.Setup(i => i.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateSearchResult(true, available, ["KB1"]));
        return m;
    }

    private static FleetUpdatesVM Build(
        Mock<INodeUpdateInstaller> installer,
        Mock<IFleetMaintenanceService> maintenance,
        bool allowInstall)
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.SetupGet(d => d.RegisteredAgents).Returns([Node]);
        var store = new Mock<INodeUpdateStatusStore>();
        store.Setup(s => s.GetAll()).Returns([]);

        return new FleetUpdatesVM(
            dispatcher.Object,
            Dispatcher.CurrentDispatcher,
            store: store.Object,
            maintenance: maintenance.Object,
            installer: installer.Object,
            maintenanceOptions: new MaintenanceOptions { AllowUpdateInstall = allowInstall });
    }

    [Fact]
    public void Options_Should_DisallowInstallByDefault()
    {
        Assert.False(new MaintenanceOptions().AllowUpdateInstall);
    }

    [Fact]
    public async Task Check_Should_StillReportAvailableUpdates_When_InstallIsParked()
    {
        var vm = Build(Installer(3), new Mock<IFleetMaintenanceService>(), allowInstall: false);

        await vm.UpdateActionCommand.ExecuteAsync(Node);

        // Reporting is the whole point of the feature; only the install verb is parked.
        Assert.Equal(NodeUpdateActionState.Available, vm.ActionFor(Node).State);
        Assert.Equal(3, vm.ActionFor(Node).AvailableCount);
    }

    [Fact]
    public async Task Install_Should_NotStartAnOperation_When_InstallIsParked()
    {
        var maintenance = new Mock<IFleetMaintenanceService>();
        var vm = Build(Installer(1), maintenance, allowInstall: false);

        await vm.UpdateActionCommand.ExecuteAsync(Node);   // check
        await vm.UpdateActionCommand.ExecuteAsync(Node);   // would install

        maintenance.Verify(
            s => s.StartInstallUpdatesAsync(It.IsAny<InstallUpdatesRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains("installed by IT", vm.ActionFor(Node).Detail);
    }

    [Fact]
    public async Task Install_Should_StartAnOperation_When_InstallIsEnabled()
    {
        var maintenance = new Mock<IFleetMaintenanceService>();
        maintenance
            .Setup(s => s.StartInstallUpdatesAsync(It.IsAny<InstallUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        var vm = Build(Installer(1), maintenance, allowInstall: true);

        await vm.UpdateActionCommand.ExecuteAsync(Node);
        await vm.UpdateActionCommand.ExecuteAsync(Node);

        maintenance.Verify(
            s => s.StartInstallUpdatesAsync(It.IsAny<InstallUpdatesRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
