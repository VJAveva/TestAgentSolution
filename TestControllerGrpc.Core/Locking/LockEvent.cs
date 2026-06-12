namespace TestControllerGrpc.Locking;

/// <summary>
/// Event emitted by LockRegistry on any state transition.
/// Consumed by LockBroadcaster for SignalR dispatch.
/// </summary>
public sealed record LockEvent(LockEventKind EventKind, PipelineLock Lock, OwnerIdentity? PriorOwner = null);

public enum LockEventKind
{
    Acquired,
    Released,
    Expired,
    ForceReleased,
    Rewritten
}
