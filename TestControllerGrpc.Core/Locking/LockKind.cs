namespace TestControllerGrpc.Locking;

/// <summary>
/// The type of lock held on a pipeline.
/// </summary>
public enum LockKind
{
    Trigger = 0,
    Retry = 1
}
