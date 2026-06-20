using Microsoft.Extensions.Options;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;

namespace TestControllerGrpc.Tests.Rbac;

public sealed class LockRegistryTests
{
    private readonly LockRegistry _registry;

    public LockRegistryTests()
    {
        _registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 30 }));
    }

    private static OwnerIdentity Alice => new("alice-001", "Alice", ClientKind.Wpf);
    private static OwnerIdentity Bob => new("bob-002", "Bob", ClientKind.Web);
    private static OwnerIdentity AliceWeb => new("alice-001", "Alice (Web)", ClientKind.Web);

    [Fact]
    public void TryAcquire_Should_ReturnSuccess_When_PipelineUnlocked()
    {
        var result = _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        Assert.IsType<AcquireResult.Success>(result);
        var success = (AcquireResult.Success)result;
        Assert.Equal("pipeline-1", success.Lock.PipelineId);
        Assert.Equal(Alice, success.Lock.Owner);
        Assert.Equal(LockKind.Trigger, success.Lock.Kind);
        Assert.Equal(LockStatus.Active, success.Lock.Status);
    }

    [Fact]
    public void TryAcquire_Should_ReturnConflict_When_DifferentUserHoldsLock()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var result = _registry.TryAcquire("pipeline-1", Bob, LockKind.Trigger);

        Assert.IsType<AcquireResult.Conflict>(result);
        var conflict = (AcquireResult.Conflict)result;
        Assert.Equal(Alice, conflict.ExistingLock.Owner);
    }

    [Fact]
    public void TryAcquire_Should_ReturnConflict_When_SameUserIdReacquires()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        // Single-run: ANY active lock blocks a second trigger, the owner included.
        var result = _registry.TryAcquire("pipeline-1", AliceWeb, LockKind.Retry);

        Assert.IsType<AcquireResult.Conflict>(result);
        var conflict = (AcquireResult.Conflict)result;
        Assert.Equal(Alice, conflict.ExistingLock.Owner);
    }

    [Fact]
    public void TryAcquire_Should_ReturnSuccess_When_ExistingLockExpired()
    {
        var registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 0 }));

        registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        Thread.Sleep(50); // Let it expire

        var result = registry.TryAcquire("pipeline-1", Bob, LockKind.Trigger);

        Assert.IsType<AcquireResult.Success>(result);
        var success = (AcquireResult.Success)result;
        Assert.Equal(Bob, success.Lock.Owner);
    }

    [Fact]
    public void TryRelease_Should_ReturnTrue_When_TokenMatches()
    {
        var acquired = (AcquireResult.Success)_registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var released = _registry.TryRelease("pipeline-1", acquired.Lock.Token);

        Assert.True(released);
        Assert.Null(_registry.Get("pipeline-1"));
    }

    [Fact]
    public void TryRelease_Should_ReturnFalse_When_TokenMismatches()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var released = _registry.TryRelease("pipeline-1", "not-the-real-token");

        Assert.False(released);
        Assert.NotNull(_registry.Get("pipeline-1"));
    }

    [Fact]
    public void TryRelease_Should_ReturnFalse_When_NoPipelineLockExists()
    {
        var released = _registry.TryRelease("nonexistent", "any-token");

        Assert.False(released);
    }

    [Fact]
    public void Heartbeat_Should_ExtendExpiry_When_OwnerHeartbeats()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        var lockBefore = _registry.Get("pipeline-1")!;

        Thread.Sleep(10);
        var success = _registry.Heartbeat("pipeline-1", Alice);
        var lockAfter = _registry.Get("pipeline-1")!;

        Assert.True(success);
        Assert.True(lockAfter.ExpiresUtc > lockBefore.ExpiresUtc);
    }

    [Fact]
    public void Heartbeat_Should_ReturnFalse_When_DifferentOwner()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var success = _registry.Heartbeat("pipeline-1", Bob);

        Assert.False(success);
    }

    [Fact]
    public void Heartbeat_Should_ReturnFalse_When_NoLockExists()
    {
        var success = _registry.Heartbeat("nonexistent", Alice);

        Assert.False(success);
    }

    [Fact]
    public void TryRenew_Should_ExtendExpiry_When_TokenMatches()
    {
        var acquired = (AcquireResult.Success)_registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        var expiresBefore = acquired.Lock.ExpiresUtc;

        Thread.Sleep(10);
        var renewed = _registry.TryRenew("pipeline-1", acquired.Lock.Token);
        var lockAfter = _registry.Get("pipeline-1")!;

        Assert.True(renewed);
        Assert.True(lockAfter.ExpiresUtc > expiresBefore);
    }

    [Fact]
    public void TryRenew_Should_ReturnFalse_When_TokenMismatches()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var renewed = _registry.TryRenew("pipeline-1", "not-the-real-token");

        Assert.False(renewed);
    }

    [Fact]
    public void TryRenew_Should_ReturnFalse_When_NoPipelineLockExists()
    {
        var renewed = _registry.TryRenew("nonexistent", "any-token");

        Assert.False(renewed);
    }

    [Fact]
    public void TryRenew_Should_KeepLockAlive_Through_ExpireStale()
    {
        // A short TTL would normally let the sweeper reap the lock; renewing first keeps
        // it alive (Impediment #1: a live run renews so the sweeper never reaps mid-run).
        var registry = new LockRegistry(Options.Create(
            new LockOptions { HeartbeatTimeoutSeconds = 1 }));
        var acquired = (AcquireResult.Success)registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        var renewed = registry.TryRenew("pipeline-1", acquired.Lock.Token);
        var expired = registry.ExpireStale();

        Assert.True(renewed);
        Assert.Equal(0, expired);
        Assert.NotNull(registry.Get("pipeline-1"));
    }

    [Fact]
    public void RenewalInterval_Should_BeLessThan_HeartbeatTimeout()
    {
        // Renewal must fire comfortably before the TTL elapses, or the sweeper reaps mid-run.
        var registry = new LockRegistry(Options.Create(
            new LockOptions { HeartbeatTimeoutSeconds = 30, RenewalIntervalSeconds = 10 }));

        Assert.Equal(TimeSpan.FromSeconds(10), registry.RenewalInterval);
        Assert.True(registry.RenewalInterval < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Get_Should_ReturnNull_When_LockExpired()
    {
        var registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 0 }));
        registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        Thread.Sleep(50);
        var lockEntry = registry.Get("pipeline-1");

        Assert.Null(lockEntry);
    }

    [Fact]
    public void GetAll_Should_ReturnOnlyActiveLocks()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _registry.TryAcquire("pipeline-2", Bob, LockKind.Trigger);

        var all = _registry.GetAll();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ForceRelease_Should_RemoveLock_When_Called()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        _registry.ForceRelease("pipeline-1");

        Assert.Null(_registry.Get("pipeline-1"));
    }

    [Fact]
    public void ForceRelease_Should_EmitForceReleasedEvent()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        LockEvent? capturedEvent = null;
        _registry.OnLockEvent += e => { if (e.EventKind == LockEventKind.ForceReleased) capturedEvent = e; };

        _registry.ForceRelease("pipeline-1");

        Assert.NotNull(capturedEvent);
        Assert.Equal(LockEventKind.ForceReleased, capturedEvent!.EventKind);
        Assert.Equal(Alice, capturedEvent.PriorOwner);
    }

    [Fact]
    public void ExpireStale_Should_ExpireTimedOutLocks()
    {
        var registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 0 }));
        registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        Thread.Sleep(50);
        var expired = registry.ExpireStale();

        Assert.Equal(1, expired);
        Assert.Null(registry.Get("pipeline-1"));
    }

    [Fact]
    public void ExpireStale_Should_EmitExpiredEvent()
    {
        var registry = new LockRegistry(Options.Create(new LockOptions { HeartbeatTimeoutSeconds = 0 }));
        registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        LockEvent? capturedEvent = null;
        registry.OnLockEvent += e => { if (e.EventKind == LockEventKind.Expired) capturedEvent = e; };

        Thread.Sleep(50);
        registry.ExpireStale();

        Assert.NotNull(capturedEvent);
        Assert.Equal(LockEventKind.Expired, capturedEvent!.EventKind);
        Assert.Equal(Alice, capturedEvent.PriorOwner);
    }

    [Fact]
    public void RewriteOwners_Should_UpdateAllLockOwners()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _registry.TryAcquire("pipeline-2", Bob, LockKind.Trigger);

        var defaultOwner = new OwnerIdentity(
            DefaultUser.UserId.ToString("D"), "Default user", ClientKind.Wpf);
        _registry.RewriteOwners(defaultOwner);

        var lock1 = _registry.Get("pipeline-1")!;
        var lock2 = _registry.Get("pipeline-2")!;
        Assert.Equal(defaultOwner, lock1.Owner);
        Assert.Equal(defaultOwner, lock2.Owner);
    }

    [Fact]
    public void RewriteOwners_Should_EmitRewrittenEvent_PerEntry()
    {
        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        _registry.TryAcquire("pipeline-2", Bob, LockKind.Trigger);
        var events = new List<LockEvent>();
        _registry.OnLockEvent += e => { if (e.EventKind == LockEventKind.Rewritten) events.Add(e); };

        var defaultOwner = new OwnerIdentity(
            DefaultUser.UserId.ToString("D"), "Default user", ClientKind.Wpf);
        _registry.RewriteOwners(defaultOwner);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.PriorOwner == Alice);
        Assert.Contains(events, e => e.PriorOwner == Bob);
    }

    [Fact]
    public void TryAcquire_Should_EmitAcquiredEvent()
    {
        LockEvent? capturedEvent = null;
        _registry.OnLockEvent += e => capturedEvent = e;

        _registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);

        Assert.NotNull(capturedEvent);
        Assert.Equal(LockEventKind.Acquired, capturedEvent!.EventKind);
        Assert.Equal("pipeline-1", capturedEvent.Lock.PipelineId);
    }

    [Fact]
    public void TryRelease_Should_EmitReleasedEvent()
    {
        var acquired = (AcquireResult.Success)_registry.TryAcquire("pipeline-1", Alice, LockKind.Trigger);
        LockEvent? capturedEvent = null;
        _registry.OnLockEvent += e => capturedEvent = e;

        _registry.TryRelease("pipeline-1", acquired.Lock.Token);

        Assert.NotNull(capturedEvent);
        Assert.Equal(LockEventKind.Released, capturedEvent!.EventKind);
    }

    [Fact]
    public void OwnerIdentity_Should_EqualOnUserIdOnly()
    {
        // Alice on WPF == Alice on Web (same UserId)
        Assert.Equal(Alice, AliceWeb);
        Assert.NotEqual(Alice, Bob);
    }

    [Fact]
    public void DefaultMode_Should_BlockSecondWpfInstance()
    {
        // Per single-run semantics: even two WPF instances sharing the Default user
        // cannot start a second concurrent run — the second trigger is refused.
        var defaultWpf1 = new OwnerIdentity(
            DefaultUser.UserId.ToString("D"), "Default user", ClientKind.Wpf);
        var defaultWpf2 = new OwnerIdentity(
            DefaultUser.UserId.ToString("D"), "Default user", ClientKind.Wpf);

        _registry.TryAcquire("pipeline-1", defaultWpf1, LockKind.Trigger);
        var result = _registry.TryAcquire("pipeline-1", defaultWpf2, LockKind.Trigger);

        Assert.IsType<AcquireResult.Conflict>(result);
    }
}
