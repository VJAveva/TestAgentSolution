namespace TestControllerGrpc.Identity;

/// <summary>
/// Identifies the transport/client that originated the request.
/// Drives Default-mode authorization decisions.
/// </summary>
public enum ClientKind
{
    Wpf = 0,
    Web = 1,
    Cli = 2
}
