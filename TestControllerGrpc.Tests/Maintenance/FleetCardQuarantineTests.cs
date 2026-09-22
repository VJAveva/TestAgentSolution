using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Quarantine is sticky, so a node whose agent reconnected after the wait timed out keeps the flag. The card
/// has to say that, otherwise it is indistinguishable from a machine that is genuinely down.
/// </summary>
public class FleetCardQuarantineTests
{
    private static FleetCardVM Card(bool quarantined, bool error) =>
        new() { AgentName = "JVGR2", IsQuarantined = quarantined, IsError = error };

    [Fact]
    public void QuarantineHint_Should_BeEmpty_When_NotQuarantined()
    {
        Assert.Equal("", Card(quarantined: false, error: false).QuarantineHint);
    }

    [Fact]
    public void QuarantineHint_Should_SayAgentIsBack_When_QuarantinedButHealthy()
    {
        var card = Card(quarantined: true, error: false);

        Assert.True(card.IsQuarantinedAndOnline);
        Assert.Contains("back online", card.QuarantineHint);
    }

    [Fact]
    public void QuarantineHint_Should_SayOffline_When_QuarantinedAndUnreachable()
    {
        var card = Card(quarantined: true, error: true);

        Assert.False(card.IsQuarantinedAndOnline);
        Assert.Contains("offline", card.QuarantineHint);
    }

    [Fact]
    public void QuarantineHint_Should_Refresh_When_AgentComesBackOnline()
    {
        // The card is built while the machine is still down, then health recovers: without a change
        // notification the hint would keep saying "offline" forever.
        var card = Card(quarantined: true, error: true);
        var changed = new List<string>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        card.IsError = false;

        Assert.Contains(nameof(FleetCardVM.QuarantineHint), changed);
        Assert.Contains(nameof(FleetCardVM.IsQuarantinedAndOnline), changed);
        Assert.Contains("back online", card.QuarantineHint);
    }

    [Fact]
    public void QuarantineHint_Should_Clear_When_OperatorReturnsNodeToRotation()
    {
        var card = Card(quarantined: true, error: false);
        var changed = new List<string>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        card.IsQuarantined = false;

        Assert.Contains(nameof(FleetCardVM.QuarantineHint), changed);
        Assert.Equal("", card.QuarantineHint);
    }
}
