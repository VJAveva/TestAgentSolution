using System.Text.Json;
using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.Tests.Maintenance;

public class WindowsUpdateEventMapperTests
{
    private const string Node = "JVKPRI";
    private static readonly DateTimeOffset Detected = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static string Serialize(WindowsUpdatePayload payload)
        => JsonSerializer.Serialize(payload, WindowsUpdateEventMapper.JsonOptions);

    [Fact]
    public void TryMap_Should_PreserveKindAndSource_When_AgentSuppliesThem()
    {
        var json = Serialize(new WindowsUpdatePayload
        {
            Kind = MaintenanceEventKind.UpdateFailed,
            Source = MaintenanceEventSource.EventLog,
            PendingCount = 2,
            DetectedUtc = Detected,
        });

        var evt = WindowsUpdateEventMapper.TryMap(Node, json, DateTimeOffset.UtcNow);

        Assert.NotNull(evt);
        Assert.Equal(MaintenanceEventKind.UpdateFailed, evt!.Kind);
        Assert.Equal(MaintenanceEventSource.EventLog, evt.Source);
        Assert.Equal(Detected, evt.DetectedUtc);
    }

    [Fact]
    public void TryMap_Should_DeriveKindFromPosture_When_KindUnspecified()
    {
        var json = Serialize(new WindowsUpdatePayload { RebootRequired = true, DetectedUtc = Detected });

        var evt = WindowsUpdateEventMapper.TryMap(Node, json, DateTimeOffset.UtcNow);

        Assert.Equal(MaintenanceEventKind.RebootRequired, evt!.Kind);
        Assert.True(evt.Status.RebootRequired);
    }

    [Fact]
    public void TryMap_Should_DerivePending_When_OnlyPendingCountSet()
    {
        var json = Serialize(new WindowsUpdatePayload { PendingCount = 3, DetectedUtc = Detected });

        var evt = WindowsUpdateEventMapper.TryMap(Node, json, DateTimeOffset.UtcNow);

        Assert.Equal(MaintenanceEventKind.UpdatePending, evt!.Kind);
        Assert.Equal(3, evt.Status.PendingCount);
    }

    [Fact]
    public void TryMap_Should_CarryItemsThrough_When_AgentReportsThem()
    {
        var json = Serialize(new WindowsUpdatePayload
        {
            Kind = MaintenanceEventKind.UpdatePending,
            PendingCount = 1,
            Items = [new UpdateItemDto { KbId = "KB5034567", Title = "Cumulative Update", Result = "Pending" }],
            DetectedUtc = Detected,
        });

        var evt = WindowsUpdateEventMapper.TryMap(Node, json, DateTimeOffset.UtcNow);

        var item = Assert.Single(evt!.Status.Items);
        Assert.Equal("KB5034567", item.KbId);
        Assert.Equal("Pending", item.Result);
    }

    [Fact]
    public void TryMap_Should_FallBackToReceivedTime_When_DetectedUtcMissing()
    {
        var received = new DateTimeOffset(2026, 2, 2, 8, 0, 0, TimeSpan.Zero);

        var evt = WindowsUpdateEventMapper.TryMap(Node, """{"rebootRequired":true}""", received);

        Assert.Equal(received, evt!.DetectedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    public void TryMap_Should_ReturnNull_When_DetailMissingOrMalformed(string? detail)
    {
        Assert.Null(WindowsUpdateEventMapper.TryMap(Node, detail, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryMap_Should_ReturnNull_When_NodeIdBlank()
    {
        var json = Serialize(new WindowsUpdatePayload { RebootRequired = true });

        Assert.Null(WindowsUpdateEventMapper.TryMap("", json, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("2026-01 Cumulative Update for Windows (KB5034567)", "KB5034567")]
    [InlineData("Security Intelligence Update kb2267602", "KB2267602")]
    [InlineData("Update with no article id", "")]
    [InlineData("KB with no digits", "")]
    [InlineData(null, "")]
    public void ExtractKb_Should_ReturnArticleId_When_TitleEmbedsIt(string? title, string expected)
    {
        Assert.Equal(expected, WindowsUpdateEventMapper.ExtractKb(title));
    }

    [Fact]
    public void Apply_Should_YieldRebootRequiredState_When_MappedRebootEventApplied()
    {
        var json = Serialize(new WindowsUpdatePayload
        {
            Kind = MaintenanceEventKind.RebootRequired,
            Source = MaintenanceEventSource.RegistryPoll,
            RebootRequired = true,
            DetectedUtc = Detected,
        });
        var store = new NodeUpdateStatusStore();

        store.Apply(WindowsUpdateEventMapper.TryMap(Node, json, DateTimeOffset.UtcNow)!);

        Assert.Equal(WindowsUpdateState.RebootRequired, store.Get(Node)!.State);
    }
}
