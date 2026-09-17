using System.Text.Json;
using TestAgentDisplay.Services;
using Xunit;

namespace TestAgentDisplay.Tests;

/// <summary>
/// Pins the EVENT_WINDOWS_UPDATE wire contract. This payload is produced by the agent
/// (TestAgentGrpc/Services/WindowsUpdateReporter.cs) using camelCase JSON; these tests use the
/// literal shape rather than a shared type, because TestAgentDisplay ships self-contained and
/// deliberately does not reference TestControllerGrpc.Core.
/// </summary>
public class WindowsUpdateSnapshotTests
{
    private const string RealisticPayload = """
    {
      "kind": "RebootRequired",
      "source": "Registry",
      "rebootRequired": true,
      "pendingCount": 3,
      "lastInstallUtc": "2026-09-16T04:12:00+00:00",
      "detectedUtc": "2026-09-17T06:30:00+00:00",
      "items": [
        { "kbId": "KB5034123", "title": "Cumulative Update", "result": "Installed", "resultCode": "" },
        { "kbId": "KB5034999", "title": "Security Update",  "result": "Failed",    "resultCode": "0x80073712" }
      ]
    }
    """;

    [Fact]
    public void TryParse_Should_ReadPosture_When_PayloadIsTheAgentsRealShape()
    {
        Assert.True(WindowsUpdateSnapshot.TryParse(RealisticPayload, out var snap));

        Assert.True(snap.RebootRequired);
        Assert.Equal(3, snap.PendingCount);
        Assert.Equal(2, snap.Items.Count);
        Assert.Equal("KB5034999", snap.Items[1].KbId);
        Assert.Equal("0x80073712", snap.Items[1].ResultCode);
    }

    [Fact]
    public void TryParse_Should_IgnoreUnknownEnumFields_When_KindAndSourceArePresent()
    {
        // Kind/Source are intentionally NOT modelled. If they were, a change from name to number
        // serialization would break parsing; this proves the display is immune to that.
        Assert.True(WindowsUpdateSnapshot.TryParse(RealisticPayload, out _));

        var numericEnums = RealisticPayload
            .Replace("\"kind\": \"RebootRequired\"", "\"kind\": 5")
            .Replace("\"source\": \"Registry\"", "\"source\": 1");
        Assert.True(WindowsUpdateSnapshot.TryParse(numericEnums, out var snap));
        Assert.True(snap.RebootRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("{ \"rebootRequired\": ")]
    public void TryParse_Should_ReturnFalse_When_PayloadIsUnusable(string? payload)
    {
        Assert.False(WindowsUpdateSnapshot.TryParse(payload, out var snap));
        Assert.False(snap.RebootRequired);   // out value is still safe to touch
    }

    [Fact]
    public void TryParse_Should_DefaultMissingFields_When_PayloadIsMinimal()
    {
        Assert.True(WindowsUpdateSnapshot.TryParse("{}", out var snap));

        Assert.False(snap.RebootRequired);
        Assert.Equal(0, snap.PendingCount);
        Assert.Empty(snap.Items);
        Assert.Null(snap.LastInstallUtc);
    }

    [Theory]
    [InlineData(true, 3, "Reboot required (3 pending)")]
    [InlineData(true, 0, "Reboot required")]
    [InlineData(false, 2, "2 update(s) pending")]
    [InlineData(false, 0, "Up to date")]
    public void Summarize_Should_DescribePosture_When_StatesVary(bool reboot, int pending, string expected)
    {
        var snap = new WindowsUpdateSnapshot { RebootRequired = reboot, PendingCount = pending };
        Assert.Equal(expected, snap.Summarize());
    }

    [Fact]
    public void TryParse_Should_AcceptPascalCase_When_ProducerOmitsCamelCasePolicy()
    {
        // PropertyNameCaseInsensitive guards against the producer's naming policy changing.
        const string pascal = """{ "RebootRequired": true, "PendingCount": 7 }""";

        Assert.True(WindowsUpdateSnapshot.TryParse(pascal, out var snap));
        Assert.True(snap.RebootRequired);
        Assert.Equal(7, snap.PendingCount);
    }
}
