using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Workstream B / P23 persistence. WatchList.xml drives production on every agent and is in the deploy
/// preserve list, so the load path must be bit-compatible with files written by older builds.
/// </summary>
public class WatchListSkipSerializationTests
{
    private const string LegacyXml = """
        <WatchList>
          <WatchItem Tag="Nightly" Path="C:\drop" Filter="*.trg">
            <Event Type="Renamed" ExecutionType="Sequential">
              <ActionGroup Tag="Setup" ExecutionType="Sequential">
                <Action Type="RunCommand" Command="cmd.exe" Parameters="/c echo hi" Tag="Step1" />
              </ActionGroup>
            </Event>
          </WatchItem>
        </WatchList>
        """;

    [Fact]
    public void Deserialize_Should_DefaultToNotSkipped_When_LegacyFileHasNoAttributes()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(LegacyXml)!;

        WatchItemConfig wi = Assert.Single(config.WatchItems);
        Assert.False(wi.Skip);
        Assert.Null(wi.Comment);
        Assert.Null(wi.SkipReason);

        EventConfig evt = Assert.Single(wi.Events);
        Assert.False(evt.Skip);

        var group = Assert.IsType<ActionGroupConfig>(evt.Children[0]);
        Assert.False(group.Skip);
        Assert.False(Assert.IsType<ActionConfig>(group.Children[0]).Skip);
    }

    [Fact]
    public void Serialize_Should_NotEmitSkipAttributes_When_NothingSkipped()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(LegacyXml)!;

        string xml = WatchListXmlParser.SerializeWatchList(config);

        // A file with nothing skipped must not gain noise on save.
        Assert.DoesNotContain("Skip=", xml);
        Assert.DoesNotContain("<Comment>", xml);
    }

    [Fact]
    public void RoundTrip_Should_PreserveSkipAndComment()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(LegacyXml)!;
        WatchItemConfig wi = config.WatchItems[0];
        var group = (ActionGroupConfig)wi.Events[0].Children[0];
        var action = (ActionConfig)group.Children[0];

        group.Skip = true;
        group.SkipReason = "hardware unavailable";
        group.SkippedBy = "DOMAIN\\alice";
        group.SkippedAtUtc = new DateTimeOffset(2026, 9, 7, 10, 30, 0, TimeSpan.Zero);
        action.Comment = "Runs the smoke suite; keep before the revert.";
        wi.Comment = "Nightly regression entry point.";

        string xml = WatchListXmlParser.SerializeWatchList(config);
        WatchListConfig reloaded = WatchListXmlParser.DeserializeWatchList(xml)!;

        var reloadedGroup = (ActionGroupConfig)reloaded.WatchItems[0].Events[0].Children[0];
        var reloadedAction = (ActionConfig)reloadedGroup.Children[0];

        Assert.True(reloadedGroup.Skip);
        Assert.Equal("hardware unavailable", reloadedGroup.SkipReason);
        Assert.Equal("DOMAIN\\alice", reloadedGroup.SkippedBy);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 10, 30, 0, TimeSpan.Zero), reloadedGroup.SkippedAtUtc);
        Assert.Equal("Runs the smoke suite; keep before the revert.", reloadedAction.Comment);
        Assert.Equal("Nightly regression entry point.", reloaded.WatchItems[0].Comment);
    }

    [Fact]
    public void RoundTrip_Should_NotCascadeSkipToChildren()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(LegacyXml)!;
        var group = (ActionGroupConfig)config.WatchItems[0].Events[0].Children[0];
        group.Skip = true;

        WatchListConfig reloaded = WatchListXmlParser.DeserializeWatchList(
            WatchListXmlParser.SerializeWatchList(config))!;

        var reloadedGroup = (ActionGroupConfig)reloaded.WatchItems[0].Events[0].Children[0];

        // Only the clicked node carries the flag; the cascade is computed at traversal time.
        Assert.True(reloadedGroup.Skip);
        Assert.False(((ActionConfig)reloadedGroup.Children[0]).Skip);
    }

    [Fact]
    public void SingleWatchItem_RoundTrip_Should_PreserveSkip()
    {
        // The WatchItem-scoped serializer is a separate code path from the whole-list one.
        WatchItemConfig wi = WatchListXmlParser.DeserializeWatchList(LegacyXml)!.WatchItems[0];
        wi.Skip = true;
        wi.SkipReason = "paused";

        WatchItemConfig reloaded = WatchListXmlParser.DeserializeWatchItem(
            WatchListXmlParser.SerializeWatchItem(wi))!;

        Assert.True(reloaded.Skip);
        Assert.Equal("paused", reloaded.SkipReason);
    }

    [Fact]
    public void Manifest_Should_ReflectPersistedSkip()
    {
        WatchListConfig config = WatchListXmlParser.DeserializeWatchList(LegacyXml)!;
        ((ActionGroupConfig)config.WatchItems[0].Events[0].Children[0]).Skip = true;

        WatchListConfig reloaded = WatchListXmlParser.DeserializeWatchList(
            WatchListXmlParser.SerializeWatchList(config))!;

        IReadOnlyList<SkippedNode> manifest = SkipEvaluator.BuildManifest(reloaded.WatchItems[0]);

        Assert.Equal(2, manifest.Count);
        Assert.Equal(SkipOrigin.Explicit, manifest[0].State.Origin);
        Assert.Equal(SkipOrigin.Inherited, manifest[1].State.Origin);
    }
}
