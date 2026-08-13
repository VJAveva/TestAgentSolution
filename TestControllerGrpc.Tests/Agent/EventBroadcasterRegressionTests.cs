extern alias AgentAlias;
using Microsoft.Extensions.Logging.Abstractions;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;

namespace TestControllerGrpc.Tests.Agent;

/// <summary>
/// Regression guard for the gRPC "Unexpected compressed flag value in message
/// header" framing corruption (docs/Issues/Debug_Grpc_CompressedFlag_Error.md).
///
/// Root cause: multiple independent gRPC stream writers (the controller push
/// stream, the SubscribeAgentEvents firehose, the tray UI) each serialize the
/// SAME <see cref="ExecutionEvent"/> instance on a different thread. Concurrent
/// serialization / mutation of one shared message corrupts the 5-byte frame
/// header. The fix in <see cref="EventBroadcaster.Publish"/> hands every
/// subscriber its OWN deep clone so no two writers ever touch the same instance.
/// </summary>
public sealed class EventBroadcasterRegressionTests
{
    [Fact]
    public void Publish_Should_GiveEachSubscriberDistinctInstance_When_MultipleSubscribers()
    {
        using var broadcaster = new EventBroadcaster(NullLogger<EventBroadcaster>.Instance);
        var (reader1, sub1) = broadcaster.Subscribe();
        var (reader2, sub2) = broadcaster.Subscribe();

        var published = new ExecutionEvent
        {
            ExecutionId = "exec-1",
            EventType = ExecutionEventType.EventStdoutLine,
            OutputLine = "line",
        };

        broadcaster.Publish(published);

        Assert.True(reader1.TryRead(out var got1));
        Assert.True(reader2.TryRead(out var got2));
        Assert.NotNull(got1);
        Assert.NotNull(got2);

        // The whole point of the fix: each subscriber receives its own clone —
        // never the publisher's instance and never a shared instance between
        // subscribers. Sharing an instance is what corrupted the wire framing.
        Assert.NotSame(published, got1);
        Assert.NotSame(published, got2);
        Assert.NotSame(got1, got2);

        // Clones must still carry identical payload.
        Assert.Equal("exec-1", got1!.ExecutionId);
        Assert.Equal("exec-1", got2!.ExecutionId);
        Assert.Equal("line", got1.OutputLine);

        sub1.Dispose();
        sub2.Dispose();
    }
}
