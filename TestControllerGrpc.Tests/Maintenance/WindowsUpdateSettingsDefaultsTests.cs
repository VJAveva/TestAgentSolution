extern alias AgentAlias;

using AgentSettings = AgentAlias::TestAgentGrpc.WindowsUpdateSettings;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// A ratchet, not a framework test. A reboot-required node stops taking pipeline work, so flipping this
/// default back on silently re-blocks the fleet on a signal that is not Windows Update.
/// </summary>
public class WindowsUpdateSettingsDefaultsTests
{
    [Fact]
    public void TreatPendingFileRenamesAsRebootRequired_Should_DefaultToFalse_When_SettingsAreCreated()
    {
        // Measured 2026-09-22: every flagged agent tripped on AMP.Installer / Config.Msi rollback files,
        // and none had either servicing key set. Treating that as reboot-required blocks real runs.
        Assert.False(new AgentSettings().TreatPendingFileRenamesAsRebootRequired);
    }

    [Fact]
    public void Detection_Should_StayEnabledByDefault_When_SettingsAreCreated()
    {
        var settings = new AgentSettings();

        // Demoting the noisy signal must not turn posture reporting off altogether.
        Assert.True(settings.Enabled);
        Assert.True(settings.ScanPendingUpdates);
    }
}
