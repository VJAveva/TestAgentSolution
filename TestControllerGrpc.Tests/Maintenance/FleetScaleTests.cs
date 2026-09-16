using System.Diagnostics;
using System.Windows.Threading;
using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Phase 0c guards: the fleet panel has to stay usable at 50+ nodes. Each case here pins a property that
/// degrades silently as the fleet grows — a per-event full rebuild, an O(N²) scan, or a fixed probe cadence.
/// </summary>
public class FleetScaleTests
{
    private static Mock<IAgentGrpcDispatcher> AgentsNamed(params string[] agents)
    {
        var d = new Mock<IAgentGrpcDispatcher>();
        d.Setup(x => x.RegisteredAgents).Returns(agents);
        return d;
    }

    private static NodeMaintenanceEventDto Event(string nodeId, bool rebootRequired = false) => new()
    {
        NodeId = nodeId,
        Kind = rebootRequired ? MaintenanceEventKind.RebootRequired : MaintenanceEventKind.UpdatePending,
        Source = MaintenanceEventSource.RegistryPoll,
        Status = new WindowsUpdateStatusDto { RebootRequired = rebootRequired, PendingCount = 1 },
        DetectedUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, or the timeout expires.</summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void Rows_Should_CoalesceBurst_When_EveryNodeReportsAtOnce()
    {
        var agents = Enumerable.Range(1, 50).Select(i => $"NODE{i:00}").ToArray();
        var store = new NodeUpdateStatusStore();
        var vm = new FleetUpdatesVM(AgentsNamed(agents).Object, Dispatcher.CurrentDispatcher, store);

        var rebuilds = 0;
        vm.RowsChanged += () => rebuilds++;

        // A registry-poll burst: all 50 nodes report within the debounce window.
        foreach (var a in agents)
            store.Apply(Event(a));

        Assert.True(PumpUntil(() => rebuilds > 0));

        // Without coalescing this would be one full 50-node rebuild per event.
        Assert.True(rebuilds < 50, $"expected the burst to coalesce, got {rebuilds} rebuilds");
        Assert.Equal(50, vm.Rows.Count);
    }

    [Fact]
    public void Rows_Should_IncludeOrphans_When_NodeReportedButIsNoLongerRegistered()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("GONE1"));
        store.Apply(Event("GONE2"));

        var vm = new FleetUpdatesVM(AgentsNamed("LIVE1").Object, Dispatcher.CurrentDispatcher, store);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.AgentName == "GONE1");
        Assert.Contains(vm.Rows, r => r.AgentName == "GONE2");
        Assert.Contains(vm.Rows, r => r.AgentName == "LIVE1");
    }

    [Fact]
    public void Rows_Should_NotDuplicateRegisteredNode_When_ItAlsoHasStoreStatus()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("LIVE1"));

        var vm = new FleetUpdatesVM(AgentsNamed("LIVE1").Object, Dispatcher.CurrentDispatcher, store);

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void Rows_Should_MatchOrphanCaseInsensitively_When_RegistrationCasingDiffers()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("live1"));

        var vm = new FleetUpdatesVM(AgentsNamed("LIVE1").Object, Dispatcher.CurrentDispatcher, store);

        // Matched as the same node, so it must not appear twice under two casings.
        Assert.Single(vm.Rows);
    }

    [Theory]
    [InlineData(1, 5)]      // small fleet keeps the original cadence
    [InlineData(9, 5)]
    [InlineData(10, 5)]
    [InlineData(50, 25)]    // 50 nodes spread the same probe rate over 25 s
    [InlineData(200, 30)]   // capped so a large fleet still notices recovery
    public void ProbeInterval_Should_ScaleWithFleetSize_When_AgentCountGrows(int agents, int expectedSeconds)
    {
        var interval = FleetVM.ProbeIntervalFor(agents);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void Rows_Should_LeadWithRebootRequired_When_FleetHasMixedStates()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("ZZZ_REBOOT", rebootRequired: true));
        store.Apply(Event("AAA_PENDING"));

        var vm = new FleetUpdatesVM(
            AgentsNamed("AAA_PENDING", "MMM_QUIET", "ZZZ_REBOOT").Object, Dispatcher.CurrentDispatcher, store);

        // Alphabetically ZZZ_REBOOT sorts last; severity must float it to the top.
        Assert.Equal("ZZZ_REBOOT", vm.Rows[0].AgentName);
    }

    [Theory]
    [InlineData(UpdateRowFilter.All, 3)]
    [InlineData(UpdateRowFilter.RebootRequired, 1)]
    [InlineData(UpdateRowFilter.UpdatesPending, 1)]
    public void FilteredRows_Should_NarrowToSelectedStatus_When_FilterChanges(UpdateRowFilter filter, int expected)
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("REBOOT1", rebootRequired: true));
        store.Apply(Event("PENDING1"));

        var vm = new FleetUpdatesVM(
            AgentsNamed("REBOOT1", "PENDING1", "QUIET1").Object, Dispatcher.CurrentDispatcher, store)
        {
            RowFilter = filter,
        };

        Assert.Equal(expected, vm.FilteredRows.Count);
        Assert.Equal(3, vm.Rows.Count); // the unfiltered set is preserved
    }

    [Fact]
    public void FilterSummary_Should_ReportBothCounts_When_FilterHidesRows()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("REBOOT1", rebootRequired: true));

        var vm = new FleetUpdatesVM(
            AgentsNamed("REBOOT1", "QUIET1").Object, Dispatcher.CurrentDispatcher, store)
        {
            RowFilter = UpdateRowFilter.RebootRequired,
        };

        Assert.Equal("1 of 2 agent(s)", vm.FilterSummary);
    }

    [Fact]
    public void Banner_Should_CapNameList_When_ManyAgentsNeedReboot()
    {
        var agents = Enumerable.Range(1, 20).Select(i => $"NODE{i:00}").ToArray();
        var store = new NodeUpdateStatusStore();
        foreach (var a in agents)
            store.Apply(Event(a, rebootRequired: true));

        var vm = new FleetUpdatesVM(AgentsNamed(agents).Object, Dispatcher.CurrentDispatcher, store);

        var banner = Assert.Single(vm.Banners);
        Assert.Equal("20 agents need a reboot", banner.Title);
        Assert.Contains("and 14 more", banner.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("NODE20", banner.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_Should_ListEveryName_When_FleetIsSmall()
    {
        var store = new NodeUpdateStatusStore();
        store.Apply(Event("A1", rebootRequired: true));
        store.Apply(Event("B2", rebootRequired: true));

        var vm = new FleetUpdatesVM(AgentsNamed("A1", "B2").Object, Dispatcher.CurrentDispatcher, store);

        var banner = Assert.Single(vm.Banners);
        Assert.Contains("A1, B2", banner.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("more", banner.Detail, StringComparison.Ordinal);
    }
}
