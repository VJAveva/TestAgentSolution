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

    // ── Dismissal: a banner must clear, stay cleared through refreshes, and come back when the
    //    situation actually changes. Suppressing a CHANGED condition would hide real news.

    [Fact]
    public void DismissBanner_Should_RemoveOnlyThatBanner_When_Invoked()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        store.Apply(Event(NodeB, MaintenanceEventKind.UpdatePending, pending: 3));
        var vm = Build(store, null, NodeA, NodeB);

        vm.DismissBannerCommand.Execute(vm.Banners[0]);

        var remaining = Assert.Single(vm.Banners);
        Assert.False(remaining.IsWarning);   // the pending banner survives
    }

    [Fact]
    public void DismissBanner_Should_StayDismissed_When_TheSameAgentsStillReport()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        var vm = Build(store, null, NodeA);

        vm.DismissBannerCommand.Execute(vm.Banners[0]);
        Assert.Empty(vm.Banners);

        // Same node reports the same posture again - a rebuild must not resurrect the banner.
        // Refresh is the synchronous rebuild path; the event path is debounced by 250ms and a unit
        // test cannot advance that timer, so asserting after Drain() would pass for the wrong reason.
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Banners);
    }

    [Fact]
    public void DismissBanner_Should_Reappear_When_AnotherAgentJoinsTheCondition()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        var vm = Build(store, null, NodeA, NodeB);

        vm.DismissBannerCommand.Execute(vm.Banners[0]);
        Assert.Empty(vm.Banners);

        // A second agent now needs a reboot: the affected set changed, so this is new information.
        store.Apply(Event(NodeB, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        vm.RefreshCommand.Execute(null);

        var banner = Assert.Single(vm.Banners);
        Assert.Equal("2 agents need a reboot", banner.Title);
    }

    [Fact]
    public void DismissBanner_Should_Ignore_When_ParameterIsNull()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event(NodeA, MaintenanceEventKind.RebootRequired, rebootRequired: true));
        var vm = Build(store, null, NodeA);

        vm.DismissBannerCommand.Execute(null);

        Assert.Single(vm.Banners);
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

    private static NodeUpdateRowVM Row(UpdateScanStatus scan, int? pending) =>
        new("JVGR1", new NodeUpdateStatus
        {
            NodeId = "JVGR1",
            State = pending > 0 ? WindowsUpdateState.UpdatePending : WindowsUpdateState.Unknown,
            LastSource = MaintenanceEventSource.StartupSnapshot,
            ScanStatus = scan,
            PendingCount = pending,
            LastReportUtc = DateTimeOffset.UtcNow,
        }, TimeSpan.FromHours(6));

    [Theory]
    [InlineData(UpdateScanStatus.Failed)]
    [InlineData(UpdateScanStatus.Stale)]
    [InlineData(UpdateScanStatus.Unknown)]
    public void PendingText_Should_ShowADash_When_TheScanDidNotSucceed(UpdateScanStatus scan)
    {
        // Rendering "0" here is the whole bug: it tells an operator the node is clean when nobody looked.
        Assert.Equal("\u2014", Row(scan, pending: null).PendingText);
        Assert.Equal("\u2014", Row(scan, pending: 0).PendingText);
    }

    [Fact]
    public void PendingText_Should_ShowTheCount_When_TheScanSucceeded()
    {
        Assert.Equal("0", Row(UpdateScanStatus.Ok, pending: 0).PendingText);
        Assert.Equal("4", Row(UpdateScanStatus.Ok, pending: 4).PendingText);
    }

    [Theory]
    [InlineData(UpdateScanStatus.Failed, "Scan failed")]
    [InlineData(UpdateScanStatus.Stale, "Scan stale")]
    public void StateText_Should_NameTheScanProblem_When_TheScanDidNotSucceed(UpdateScanStatus scan, string expected)
    {
        Assert.Equal(expected, Row(scan, pending: null).StateText);
    }

    private static NodeMaintenanceEventDto ScanEvent(string nodeId, UpdateScanStatus scan, int? pending) => new()
    {
        NodeId = nodeId,
        Kind = pending > 0 ? MaintenanceEventKind.UpdatePending : MaintenanceEventKind.RebootCleared,
        Source = MaintenanceEventSource.StartupSnapshot,
        Status = new WindowsUpdateStatusDto { PendingCount = pending, ScanStatus = scan },
        DetectedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ScanUnknownCount_Should_CountNodesWhosePostureIsNotProven()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(ScanEvent(NodeA, UpdateScanStatus.Failed, pending: null));
        store.Apply(ScanEvent(NodeB, UpdateScanStatus.Ok, pending: 0));

        // NodeC never reports at all, which is also "not proven" - silence is not an all-clear.
        var vm = Build(store, null, NodeA, NodeB, "NodeC");
        Drain();

        Assert.Equal(2, vm.ScanUnknownCount);
    }

    [Fact]
    public void ScanUnknownCount_Should_BeZero_When_EveryScanSucceeded()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(ScanEvent(NodeA, UpdateScanStatus.Ok, pending: 0));
        store.Apply(ScanEvent(NodeB, UpdateScanStatus.Ok, pending: 3));

        var vm = Build(store, null, NodeA, NodeB);
        Drain();

        Assert.Equal(0, vm.ScanUnknownCount);
        Assert.Equal(1, vm.PendingCount);
    }
}
