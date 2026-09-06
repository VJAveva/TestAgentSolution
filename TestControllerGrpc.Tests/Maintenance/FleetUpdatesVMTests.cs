using System.Windows.Threading;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Maintenance;

public class FleetUpdatesVMTests
{
    private const string NodeA = "JVKPRI";
    private const string NodeB = "JVGR1";

    private static FleetUpdatesVM Build(
        INodeUpdateStatusStore store,
        IFleetNotificationService? notifications = null,
        params string[] agents)
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.Setup(d => d.RegisteredAgents).Returns(agents);
        return new FleetUpdatesVM(dispatcher.Object, Dispatcher.CurrentDispatcher, store, notifications);
    }

    private static NodeMaintenanceEventDto Event(string nodeId, MaintenanceEventKind kind,
        bool rebootRequired = false, int pending = 0)
        => new()
        {
            NodeId = nodeId,
            Kind = kind,
            Source = MaintenanceEventSource.RegistryPoll,
            Status = new WindowsUpdateStatusDto { RebootRequired = rebootRequired, PendingCount = pending },
            DetectedUtc = DateTimeOffset.UtcNow,
        };

    // The VM marshals store/notification callbacks through Dispatcher.InvokeAsync; a unit test has no message
    // pump, so push a frame to run whatever is queued.
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Rows_Should_IncludeAgentsWithNothingToReport_When_StoreIsEmpty()
    {
        var vm = Build(new NodeUpdateStatusStore(), null, NodeA, NodeB);

        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.False(r.HasReported));
        Assert.All(vm.Rows, r => Assert.Equal("Not reported", r.StateText));
        Assert.Equal(0, vm.ReportingCount);
    }

    [Fact]
    public void Rows_Should_MarkNotReportedNodeStale_When_NeverCheckedIn()
    {
        var vm = Build(new NodeUpdateStatusStore(), null, NodeA);

        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsStale);
        Assert.Equal("never", row.LastReport);
    }

    [Fact]
    public void Banners_Should_RaiseOneWarningBanner_When_MultipleNodesNeedReboot()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        store.Apply(Event(NodeB, MaintenanceEventKind.RebootRequired, rebootRequired: true));

        var vm = Build(store, null, NodeA, NodeB);

        var banner = Assert.Single(vm.Banners);
        Assert.True(banner.IsWarning);
        Assert.Equal("2 agents need a reboot", banner.Title);
        Assert.Contains("no new work will be sent", banner.Detail);
        Assert.Equal(2, vm.RebootRequiredCount);
    }

    [Fact]
    public void Banners_Should_SeparatePendingFromRebootRequired_When_BothPresent()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        store.Apply(Event(NodeB, MaintenanceEventKind.UpdatePending, pending: 3));

        var vm = Build(store, null, NodeA, NodeB);

        Assert.Equal(2, vm.Banners.Count);
        Assert.True(vm.Banners[0].IsWarning);
        Assert.False(vm.Banners[1].IsWarning);
        Assert.Contains("they still accept work", vm.Banners[1].Detail);
    }

    [Fact]
    public void Banners_Should_BeEmpty_When_AllNodesUpToDate()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootCleared));

        var vm = Build(store, null, NodeA);

        Assert.Empty(vm.Banners);
        Assert.False(vm.HasBanners);
    }

    [Fact]
    public void Row_Should_ExposeRebootBadgeAndDrainingDispatch_When_RebootRequired()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));

        var vm = Build(store, null, NodeA);

        var row = Assert.Single(vm.Rows);
        Assert.True(row.HasBadge);
        Assert.True(row.IsRebootRequired);
        Assert.Equal("Reboot required", row.BadgeText);
        Assert.Equal("Draining", row.DispatchText);
    }

    [Fact]
    public void Row_Should_ExposePendingCountBadge_When_UpdatesPending()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.UpdatePending, pending: 4));

        var vm = Build(store, null, NodeA);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("4 pending", row.BadgeText);
        Assert.Equal("Eligible", row.DispatchText);
    }

    [Fact]
    public void UnreadCount_Should_DropToZero_When_MarkAllReadInvoked()
    {
        var notifications = new FleetNotificationService();
        notifications.Raise(new FleetNotification
        {
            NodeId = NodeA,
            Kind = MaintenanceEventKind.RebootRequired,
            Title = "Reboot required",
        });

        var vm = Build(new NodeUpdateStatusStore(), notifications, NodeA);
        Assert.Equal(1, vm.UnreadCount);
        Assert.True(vm.HasNotifications);

        vm.MarkAllReadCommand.Execute(null);
        Drain();

        Assert.Equal(0, vm.UnreadCount);
        Assert.False(vm.HasUnread);
    }

    [Fact]
    public void Rows_Should_IncludeReportingNode_When_NoLongerRegistered()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeB, MaintenanceEventKind.RebootRequired, rebootRequired: true));

        var vm = Build(store, null, NodeA);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.AgentName == NodeB && r.IsRebootRequired);
    }
}
