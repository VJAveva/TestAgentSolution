namespace TestControllerGrpc.Locking;

/// <summary>
/// Terminal state of a pipeline lock.
/// </summary>
public enum LockStatus
{
    Active = 0,
    Released = 1,
    Expired = 2,
    ForceReleased = 3
}
