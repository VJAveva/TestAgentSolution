using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests;

/// <summary>
/// CORE-003: Tests that session cancellation is properly encapsulated
/// and that callers cannot accidentally dispose a shared CTS.
/// </summary>
public class ExecutionSessionCancellationTests
{
    [Fact]
    public void NewSession_IsNotCancelled()
    {
        var session = new ExecutionSession();

        Assert.False(session.IsCancellationRequested);
        Assert.False(session.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void RequestCancellation_SetsCancellationToken()
    {
        var session = new ExecutionSession();

        session.RequestCancellation();

        Assert.True(session.IsCancellationRequested);
        Assert.True(session.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancellationToken_CanBeLinkedWithExternal()
    {
        var session = new ExecutionSession();
        using var external = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            external.Token, session.CancellationToken);

        Assert.False(linked.IsCancellationRequested);

        // Session cancel propagates to linked token
        session.RequestCancellation();
        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void CancellationToken_ExternalCancelDoesNotAffectSession()
    {
        var session = new ExecutionSession();
        using var external = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            external.Token, session.CancellationToken);

        external.Cancel();

        // Linked is cancelled but session itself is not
        Assert.True(linked.IsCancellationRequested);
        Assert.False(session.IsCancellationRequested);
    }

    [Fact]
    public void DisposeCancellation_IsSafeToCallMultipleTimes()
    {
        var session = new ExecutionSession();
        session.RequestCancellation();

        // Should not throw on multiple dispose
        session.DisposeCancellation();
        session.DisposeCancellation();
        session.DisposeCancellation();
    }

    [Fact]
    public void DisposeCancellation_DoesNotAffectAlreadyObservedToken()
    {
        var session = new ExecutionSession();
        var token = session.CancellationToken;

        session.RequestCancellation();
        session.DisposeCancellation();

        // Token was already cancelled before dispose
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void MultipleSessionsCancelIndependently()
    {
        var session1 = new ExecutionSession();
        var session2 = new ExecutionSession();

        session1.RequestCancellation();

        Assert.True(session1.IsCancellationRequested);
        Assert.False(session2.IsCancellationRequested);
    }
}
