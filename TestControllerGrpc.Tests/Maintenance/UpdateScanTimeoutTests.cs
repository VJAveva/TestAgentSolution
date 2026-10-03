extern alias AgentAlias;

using Microsoft.Extensions.Logging.Abstractions;
using TestControllerGrpc.Core.Maintenance;
using Detector = AgentAlias::TestAgentGrpc.Services.WindowsUpdateDetector;
using Settings = AgentAlias::TestAgentGrpc.WindowsUpdateSettings;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Pins the behaviour of a scan that runs too long. Measured on JVGR2 on 2026-10-02: an <c>Online=false</c>
/// WUApi search took 287s against a 180s budget because the node's local update cache had not been refreshed
/// since February (<c>NoAutoUpdate=1</c> + WSUS). The TimeoutException escaped <c>PollAsync</c>, so the entire
/// poll was abandoned and the node reported NO posture at all - looking exactly like a dead agent, while the
/// reboot-required state had already been read successfully and was thrown away.
/// </summary>
public sealed class UpdateScanTimeoutTests
{
    [Fact]
    public void ScanTimedOut_Should_ReportFailed_NotAFabricatedZero()
    {
        var outcome = Detector.ScanTimedOut(NullLogger.Instance, 600);

        Assert.Equal(UpdateScanStatus.Failed, outcome.Status);
        Assert.Null(outcome.Count);
        Assert.Empty(outcome.Items);
    }

    [Fact]
    public void ScanTimedOut_Should_ExplainTheLikelyCause()
    {
        var outcome = Detector.ScanTimedOut(NullLogger.Instance, 600);

        Assert.NotNull(outcome.Error);
        Assert.Contains("600", outcome.Error!, StringComparison.Ordinal);
        Assert.Contains("NoAutoUpdate", outcome.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WUServer", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanTimeout_Default_Should_ExceedTheMeasuredWorstCase()
    {
        // 287s was measured on a real node; 180 timed out on every poll.
        const int measuredWorstCaseSeconds = 287;

        Assert.True(new Settings().ScanTimeoutSeconds > measuredWorstCaseSeconds,
            $"ScanTimeoutSeconds default is {new Settings().ScanTimeoutSeconds}s, which is below the " +
            $"{measuredWorstCaseSeconds}s measured on a node with a stale update cache - it will time out every poll.");
    }
}
