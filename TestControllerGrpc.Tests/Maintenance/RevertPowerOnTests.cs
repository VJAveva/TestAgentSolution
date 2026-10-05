using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// A baseline taken while the VM is powered OFF restores a powered-off VM. RevertPhase.PowerOn used to be a
/// progress label that assumed the revert script had powered it on - an undocumented side effect of one
/// particular script, and absent entirely from Vm-Ops.vcloud.ps1's Revert verb.
/// </summary>
public class RevertPowerOnTests
{
    private const string Node = "JVKPRI";

    private sealed class Rig
    {
        public Mock<IAgentGrpcDispatcher> Dispatcher { get; } = new();
        public Mock<IPowerShellScriptRunner> ScriptRunner { get; } = new();
        public Mock<INodeReadinessProbe> Probe { get; } = new();
        public MaintenanceStateStore StateStore { get; } = new();
        public Mock<IVirtualizationProvider> Provider { get; } = new();
        public MaintenanceOptions Options { get; }

        public Rig(bool pingOk = true, bool agentOk = true)
        {
            var dir = Path.Combine(Path.GetTempPath(), "revert-poweron", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "revert.ps1");
            File.WriteAllText(script, "# stub");

            Options = new MaintenanceOptions { RevertScriptPath = script, LogDirectory = dir };

            Dispatcher.Setup(d => d.RegisteredAgents).Returns(new[] { Node });
            Dispatcher.Setup(d => d.GetAgentAddress(Node)).Returns($"https://{Node}:5001");

            ScriptRunner
                .Setup(r => r.RunAsync(It.IsAny<ScriptInvocation>(), It.IsAny<IProgress<ScriptOutputLine>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScriptResult(0, TimeSpan.Zero, false));

            Probe.Setup(p => p.WaitForPingAsync(It.IsAny<string>(), It.IsAny<PingOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadinessResult(pingOk, TimeSpan.Zero, pingOk ? null : "no ping"));
            Probe.Setup(p => p.WaitForAgentAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReadinessResult(agentOk, TimeSpan.Zero, agentOk ? null : "agent never registered"));

            Provider.Setup(p => p.PowerOnAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string> names, CancellationToken _) =>
                    new VmOpResult([.. names.Select(n => new VmResult(n, true) { Power = VmPower.On })]));
        }

        public MachineRevertOperation Build(bool withProvider = true) => new(
            Dispatcher.Object, ScriptRunner.Object, Probe.Object, new AgentLockManager(),
            new ExecutionSessionManager(), StateStore, new Mock<IMaintenanceOperationStore>().Object,
            Options, new Mock<IAppLogger>().Object, withProvider ? Provider.Object : null);

        public Task<MaintenanceOperation> Run(bool withProvider = true) => Build(withProvider).ExecuteAsync(
            new MaintenanceOperation
            {
                Id = Guid.NewGuid(),
                NodeId = Node,
                Kind = MaintenanceKind.Revert,
                TriggerSource = MaintenanceTriggerSource.FleetPanel,
                State = MaintenanceOperationState.Queued,
                StartedUtc = DateTimeOffset.UtcNow,
            },
            new RevertRequest
            {
                NodeId = Node,
                SnapshotName = "baseline",
                TriggerSource = MaintenanceTriggerSource.FleetPanel,
                WaitForAgent = true,
            },
            new Progress<MaintenanceProgress>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task Revert_Should_PowerTheVmOn_When_TheSnapshotWasTakenPoweredOff()
    {
        var rig = new Rig();

        var result = await rig.Run();

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        rig.Provider.Verify(
            p => p.PowerOnAsync(It.Is<IReadOnlyList<string>>(n => n.Contains(Node)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Revert_Should_WaitForAgentRegistration_When_PowerOnSucceeds()
    {
        var rig = new Rig();

        await rig.Run();

        // Ping alone is not readiness: a node answers ICMP long before the agent registers.
        rig.Probe.Verify(p => p.WaitForAgentAsync(Node, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Revert_Should_Quarantine_When_TheAgentNeverRegisters()
    {
        var rig = new Rig(agentOk: false);

        var result = await rig.Run();

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.AgentWait, result.FailurePhase);
        Assert.Equal(MaintenanceState.Quarantined, rig.StateStore.Get(Node));
    }

    [Fact]
    public async Task Revert_Should_StillSucceed_When_PowerOnFailsButTheNodeComesBack()
    {
        // The revert script may already have powered it on. Readiness is judged by ping + agent, so a
        // provider misconfiguration must not turn a good revert into a quarantine.
        var rig = new Rig();
        rig.Provider.Setup(p => p.PowerOnAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vCloud credentials are not set"));

        var result = await rig.Run();

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        Assert.Equal(MaintenanceState.None, rig.StateStore.Get(Node));
    }

    [Fact]
    public async Task Revert_Should_Quarantine_When_PowerOnFailsAndTheNodeStaysDown()
    {
        var rig = new Rig(pingOk: false);
        rig.Provider.Setup(p => p.PowerOnAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmOpResult([new VmResult(Node, false, "power-on rejected")]));

        var result = await rig.Run();

        Assert.Equal(MaintenanceOperationState.Failed, result.State);
        Assert.Equal(RevertPhase.PingWait, result.FailurePhase);
    }

    [Fact]
    public async Task Revert_Should_Succeed_When_NoProviderIsConfigured()
    {
        // Pre-Phase-2 behaviour must survive: hosts without a virtualization provider still revert.
        var rig = new Rig();

        var result = await rig.Run(withProvider: false);

        Assert.Equal(MaintenanceOperationState.Succeeded, result.State);
        rig.Provider.Verify(
            p => p.PowerOnAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
