using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The Maintenance pane's in-progress card clock and the revert dialog's snapshot default.
/// </summary>
public class MaintenancePaneTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static MaintenanceOpVM ActiveRevert() => new(new MaintenanceOperation
    {
        Id = Guid.NewGuid(),
        NodeId = "jvgr1",
        Kind = MaintenanceKind.Revert,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        State = MaintenanceOperationState.Running,
        StartedUtc = Started,
    }, isActive: true);

    [Fact]
    public void Tick_Should_ShowTimeSinceStart_When_NoProgressEventArrived()
    {
        var op = ActiveRevert();

        op.Tick(Started.AddMinutes(4).AddSeconds(7));

        Assert.Equal("4:07", op.Elapsed);
    }

    [Fact]
    public void Tick_Should_ShowZero_When_ControllerClockIsBehindStart()
    {
        var op = ActiveRevert();

        op.Tick(Started.AddSeconds(-30));

        Assert.Equal("0:00", op.Elapsed);
    }

    [Fact]
    public void IsDetailsExpanded_Should_BeFalse_When_CardIsCreated()
    {
        Assert.False(ActiveRevert().IsDetailsExpanded);
    }

    [Fact]
    public void Initialize_Should_DefaultSnapshotToAgentName_When_SnapshotIsBlank()
    {
        var vm = new RevertMachineViewModel(PrecheckFails());

        vm.Initialize("jvgr1");

        Assert.Equal("jvgr1", vm.SnapshotName);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void Initialize_Should_KeepTypedSnapshot_When_SnapshotAlreadySet()
    {
        var vm = new RevertMachineViewModel(PrecheckFails()) { SnapshotName = "baseline-2026" };

        vm.Initialize("jvgr1");

        Assert.Equal("baseline-2026", vm.SnapshotName);
    }

    private static IFleetMaintenanceService PrecheckFails()
    {
        var svc = new Mock<IFleetMaintenanceService>();
        svc.Setup(s => s.PrecheckAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("offline"));
        return svc.Object;
    }
}
