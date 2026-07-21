using System.Threading.Channels;

namespace TestAgentGrpc.Services;

/// <summary>
/// Central pub-sub hub for real-time agent events.
///
/// Every execution event (stdout line, state change, heartbeat, etc.) is
/// published here. Multiple consumers — gRPC streams, the tray UI, the
/// controller event pusher — each get their own independent channel so
/// slow readers don't block the pipeline.
///
/// This is the core of the real-time monitoring: it replaces the legacy
/// <c>ActivityChange</c> event with a multi-subscriber, backpressure-aware
/// design.
/// </summary>
public sealed class EventBroadcaster : IDisposable
{
    private readonly ILogger<EventBroadcaster> _logger;
    private readonly List<ChannelWriter<ExecutionEvent>> _subscribers = new();
    private readonly object _lock = new();

    public EventBroadcaster(ILogger<EventBroadcaster> logger)
    {
        _logger = logger;
    }

    /// <summary>Number of active subscribers.</summary>
    public int SubscriberCount { get { lock (_lock) { return _subscribers.Count; } } }

    /// <summary>
    /// Creates a new subscription. Returns a <see cref="ChannelReader{T}"/>
    /// that the consumer reads from, and a dispose handle to unsubscribe.
    /// </summary>
    public (ChannelReader<ExecutionEvent> Reader, IDisposable Subscription) Subscribe(
        int capacity = 1000)
    {
        var channel = Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // Never block publishers
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_lock)
        {
            _subscribers.Add(channel.Writer);
        }

        return (channel.Reader, new Subscription(this, channel.Writer));
    }

    /// <summary>
    /// Publishes an event to every active subscriber.
    /// Non-blocking; drops if a subscriber's buffer is full.
    ///
    /// Each subscriber receives its OWN deep clone of the event. This is
    /// critical: subscribers are independent gRPC stream writers (the
    /// controller push stream, the SubscribeAgentEvents firehose, the tray UI)
    /// that each serialize the message on a different thread. Sharing one
    /// <see cref="ExecutionEvent"/> instance across them means the same object
    /// can be mutated (e.g. RunCommandStreamed stamps CorrelationId) or
    /// serialized concurrently, which corrupts the gRPC 5-byte frame header and
    /// surfaces as "Unexpected compressed flag value in message header".
    /// </summary>
    public void Publish(ExecutionEvent evt)
    {
        lock (_lock)
        {
            foreach (var writer in _subscribers)
            {
                // Clone per subscriber so no two stream writers ever touch the
                // same message instance concurrently.
                writer.TryWrite(evt.Clone());
            }
        }
    }

    private void Unsubscribe(ChannelWriter<ExecutionEvent> writer)
    {
        lock (_lock)
        {
            _subscribers.Remove(writer);
        }
        writer.TryComplete();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var w in _subscribers)
                w.TryComplete();
            _subscribers.Clear();
        }
    }

    private sealed class Subscription(EventBroadcaster owner, ChannelWriter<ExecutionEvent> writer)
        : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(writer);
    }
}
