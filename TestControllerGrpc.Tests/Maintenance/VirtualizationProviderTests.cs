using Moq;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Phase 1: the capability-aware provider seam. The cases that matter are the ones where a wrong answer is
/// silent — a VM the script never mentioned, a partial batch, and a snapshot name that the platform ignores.
/// </summary>
public class VirtualizationProviderTests
{
    private static readonly VirtualizationProviderOptions Options = new()
    {
        PlatformId = "vcloud",
        ScriptPath = @"C:\scripts\Vm-Ops.vcloud.ps1",
    };

    private sealed class FakeRunner : IPowerShellScriptRunner
    {
        private readonly string[] _stdout;
        private readonly int _exitCode;

        public FakeRunner(int exitCode, params string[] stdout)
        {
            _exitCode = exitCode;
            _stdout = stdout;
        }

        public List<ScriptInvocation> Invocations { get; } = [];

        public Task<ScriptResult> RunAsync(
            ScriptInvocation invocation, IProgress<ScriptOutputLine> output, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            foreach (var line in _stdout)
                output.Report(new ScriptOutputLine(DateTimeOffset.UtcNow, ScriptStream.Stdout, line));
            return Task.FromResult(new ScriptResult(_exitCode, TimeSpan.Zero, false));
        }
    }

    private static ScriptBackedVirtualizationProvider Build(
        FakeRunner runner, VmPlatformCapabilities? caps = null, IAppLogger? logger = null) =>
        new(runner, Options, caps ?? VmPlatformCapabilities.VCloud, logger ?? new Mock<IAppLogger>().Object);

    [Fact]
    public async Task RevertSnapshotAsync_Should_BatchIntoOneInvocation_When_PlatformSupportsIt()
    {
        var runner = new FakeRunner(0,
            """{"ok":true,"platform":"vcloud","results":[{"vm":"a","ok":true},{"vm":"b","ok":true},{"vm":"c","ok":true}]}""");

        var result = await Build(runner).RevertSnapshotAsync(["a", "b", "c"], null, CancellationToken.None);

        // One session for the whole batch is the entire point; a per-VM loop is the thing being replaced.
        var invocation = Assert.Single(runner.Invocations);
        Assert.Contains("a,b,c", invocation.Arguments);
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task InvokeAsync_Should_FallBackToPerVm_When_PlatformCannotBatch()
    {
        var runner = new FakeRunner(0, """{"ok":true,"results":[{"vm":"a","ok":true}]}""");
        var caps = VmPlatformCapabilities.VCloud with { SupportsBatchOperations = false };

        await Build(runner, caps).PowerOnAsync(["a", "b"], CancellationToken.None);

        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task ToResult_Should_ReportPartialBatch_When_SomeVmsFail()
    {
        var runner = new FakeRunner(0,
            """
            {"ok":false,"results":[{"vm":"a","ok":true},{"vm":"b","ok":false,"error":"BUSY_ENTITY","retryable":true}]}
            """);

        var result = await Build(runner).RevertSnapshotAsync(["a", "b"], null, CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Single(result.Successes);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("b", failure.VmName);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task ToResult_Should_FailUnmentionedVm_When_ScriptOmitsIt()
    {
        // Silence must never read as success: an omitted VM is an unknown outcome, not a pass.
        var runner = new FakeRunner(0, """{"ok":true,"results":[{"vm":"a","ok":true}]}""");

        var result = await Build(runner).PowerOffAsync(["a", "b"], graceful: true, CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.False(result.For("b")!.Ok);
    }

    [Fact]
    public async Task InvokeAsync_Should_UseLastJsonObject_When_ScriptInterleavesLogLines()
    {
        var runner = new FakeRunner(0,
            "[INFO] connecting to vCloud",
            "{ not really json",
            """{"ok":true,"results":[{"vm":"a","ok":true,"snapshotId":"urn:snap:1","createdUtc":"2026-09-16T10:00:00Z"}]}""");

        var result = await Build(runner).CreateSnapshotAsync(["a"], "baseline", "desc", CancellationToken.None);

        Assert.True(result.AllSucceeded);
        Assert.Equal("urn:snap:1", result.For("a")!.Snapshot!.Id);
    }

    [Fact]
    public async Task InvokeAsync_Should_FallBackToExitCode_When_ScriptEmitsNoJson()
    {
        var runner = new FakeRunner(0, "[INFO] done, no payload");

        var result = await Build(runner).PowerOnAsync(["a"], CancellationToken.None);

        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task InvokeAsync_Should_Fail_When_NoJsonAndNonZeroExitCode()
    {
        var runner = new FakeRunner(3, "[FAIL] something broke");

        var result = await Build(runner).PowerOnAsync(["a"], CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Contains("exit code 3", result.For("a")!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevertSnapshotAsync_Should_WarnAndDropName_When_PlatformHasNoNamedSnapshots()
    {
        var logger = new Mock<IAppLogger>();
        var runner = new FakeRunner(0, """{"ok":true,"results":[{"vm":"a","ok":true}]}""");

        await Build(runner, VmPlatformCapabilities.VCloud, logger.Object)
            .RevertSnapshotAsync(["a"], "Clean-SP2023R2SP1P03", CancellationToken.None);

        // The old contract accepted a name and silently ignored it; the ignore must now be visible.
        Assert.DoesNotContain("-Snapshot", runner.Invocations[0].Arguments);
        logger.Verify(l => l.Warn("Virtualization", It.Is<string>(m => m.Contains("ignored"))), Times.Once);
    }

    [Fact]
    public async Task RevertSnapshotAsync_Should_PassName_When_PlatformSupportsNamedSnapshots()
    {
        var runner = new FakeRunner(0, """{"ok":true,"results":[{"vm":"a","ok":true}]}""");

        await Build(runner, VmPlatformCapabilities.VSphere)
            .RevertSnapshotAsync(["a"], "baseline-2026-09-16", CancellationToken.None);

        Assert.Contains("-Snapshot", runner.Invocations[0].Arguments);
        Assert.Contains("baseline-2026-09-16", runner.Invocations[0].Arguments);
    }

    [Fact]
    public void Capabilities_Should_DenyBaselineRetention_When_PlatformHoldsOneSnapshot()
    {
        Assert.False(VmPlatformCapabilities.VCloud.CanRetainPreviousBaseline);
        Assert.True(VmPlatformCapabilities.VSphere.CanRetainPreviousBaseline);
        Assert.True(VmPlatformCapabilities.HyperV.CanRetainPreviousBaseline);
    }

    [Fact]
    public async Task InvokeAsync_Should_NotRunScript_When_NoVmsRequested()
    {
        var runner = new FakeRunner(0);

        var result = await Build(runner).PowerOnAsync([], CancellationToken.None);

        Assert.Empty(runner.Invocations);
        Assert.Empty(result.Results);
        Assert.False(result.AllSucceeded); // an empty batch is not a success
    }

    // ── Contract tests against the exact envelopes Vm-Ops.vcloud.ps1 emits ──────────────────────────────
    // The script builds its JSON by hand (PS 5.1 renders a one-element array as an object, which would break
    // this parser), so these pin the real wire shape rather than an idealised one.

    [Fact]
    public async Task Parser_Should_HandleCreateEnvelope_When_ScriptReportsSnapshotMetadata()
    {
        var runner = new FakeRunner(0,
            "[12:01:03] [PASS] VMware.PowerCLI 13.3.0 loaded",
            "[12:01:09] [PASS] Connected as svc-rcloud",
            """{"ok":true,"platform":"vcloud","results":[{"vm":"JVKPRI","ok":true,"snapshotId":"urn:vcloud:vm:abc/snapshot","snapshotName":"baseline-20260916-1030","createdUtc":"2026-09-16T10:30:00.0000000Z"}]}""");

        var result = await Build(runner).CreateSnapshotAsync(["JVKPRI"], "baseline-20260916-1030", "desc", CancellationToken.None);

        Assert.True(result.AllSucceeded);
        var snap = result.For("JVKPRI")!.Snapshot!;
        Assert.Equal("urn:vcloud:vm:abc/snapshot", snap.Id);
        Assert.Equal("baseline-20260916-1030", snap.Name);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 10, 30, 0, TimeSpan.Zero), snap.CreatedUtc);
    }

    [Fact]
    public async Task Parser_Should_HandleMixedBatchEnvelope_When_ScriptReportsBusyEntity()
    {
        var runner = new FakeRunner(1,
            "[12:04:11] [FAIL] jvgr2 : Revert failed - BUSY_ENTITY",
            """{"ok":false,"platform":"vcloud","results":[{"vm":"jvgr1","ok":true},{"vm":"jvgr2","ok":false,"error":"BUSY_ENTITY - unable to perform this action","retryable":true}]}""");

        var result = await Build(runner).RevertSnapshotAsync(["jvgr1", "jvgr2"], null, CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.True(result.For("jvgr1")!.Ok);
        Assert.True(result.For("jvgr2")!.Retryable);
    }

    [Fact]
    public async Task Parser_Should_HandleCredentialFailureEnvelope_When_ScriptExitsEarly()
    {
        var runner = new FakeRunner(2,
            """{"ok":false,"platform":"vcloud","error":"vCloud credentials are not set. Define RCLOUD_USER and RCLOUD_PASSWORD on the controller.","results":[{"vm":"JVKPRI","ok":false,"error":"vCloud credentials are not set. Define RCLOUD_USER and RCLOUD_PASSWORD on the controller."}]}""");

        var result = await Build(runner).PowerOnAsync(["JVKPRI"], CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Contains("RCLOUD_USER", result.For("JVKPRI")!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parser_Should_HandleStateEnvelope_When_ScriptReportsPower()
    {
        var runner = new FakeRunner(0,
            """{"ok":true,"platform":"vcloud","results":[{"vm":"a","ok":true,"power":"off"},{"vm":"b","ok":true,"power":"on"}]}""");

        var result = await Build(runner).GetPowerStateAsync(["a", "b"], CancellationToken.None);

        Assert.Equal(VmPower.Off, result.For("a")!.Power);
        Assert.Equal(VmPower.On, result.For("b")!.Power);
    }

    [Fact]
    public async Task Parser_Should_HandleEmptyListEnvelope_When_VmHasNoSnapshot()
    {
        var runner = new FakeRunner(0,
            """{"ok":true,"platform":"vcloud","results":[{"vm":"a","ok":true}]}""");

        var snapshots = await Build(runner).ListSnapshotsAsync("a", CancellationToken.None);

        // Succeeded, but nothing to report — must not surface a phantom snapshot.
        Assert.Empty(snapshots);
    }
}
