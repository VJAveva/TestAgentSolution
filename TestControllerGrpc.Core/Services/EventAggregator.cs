using System.Collections.Concurrent;

namespace TestControllerGrpc.Services;

/// <summary>
/// Loosely-coupled pub/sub event bus that replaces static events.
/// Subscribers receive an <see cref="IDisposable"/> token — disposing it
/// removes the subscription, eliminating the memory leak risk inherent
/// in static event handlers.
/// </summary>
public interface IEventAggregator
{
    /// <summary>Publishes an event to all current subscribers of <typeparamref name="TEvent"/>.</summary>
    void Publish<TEvent>(TEvent evt);

    /// <summary>
    /// Subscribes to events of type <typeparamref name="TEvent"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);
}

/// <summary>
/// Thread-safe in-process event aggregator.
/// Uses a snapshot iteration pattern so publishing is safe even if
/// handlers subscribe/unsubscribe during dispatch.
/// </summary>
public sealed class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subs = new();

    public void Publish<TEvent>(TEvent evt)
    {
        if (_subs.TryGetValue(typeof(TEvent), out var handlers))
        {
            Delegate[] snapshot;
            lock (handlers) { snapshot = [.. handlers]; }
            foreach (var h in snapshot)
                ((Action<TEvent>)h)(evt);
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        var list = _subs.GetOrAdd(typeof(TEvent), _ => new());
        lock (list) { list.Add(handler); }
        return new Unsubscriber(() => { lock (list) { list.Remove(handler); } });
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose()
        {
            Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }
}
