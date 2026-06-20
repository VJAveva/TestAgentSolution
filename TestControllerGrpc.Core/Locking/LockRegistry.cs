using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Locking;

/// <summary>
/// In-memory pipeline lock registry. Singleton, lives ONLY in the WPF controller process.
/// Thread-safe via ConcurrentDictionary + per-key locking for atomic transitions.
/// Per Pipeline_Lock_Coordination_Spec.md §4.3–4.4.
/// </summary>
public sealed class LockRegistry : ILockRegistry
{
    private readonly ConcurrentDictionary<string, PipelineLock> _locks = new();
    private readonly ConcurrentDictionary<string, object> _keyLocks = new();
    private readonly int _heartbeatTimeoutSeconds;
    private readonly TimeSpan _renewalInterval;

    public event Action<LockEvent>? OnLockEvent;

    public LockRegistry(IOptions<LockOptions> options)
    {
        _heartbeatTimeoutSeconds = options.Value.HeartbeatTimeoutSeconds;
        _renewalInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.RenewalIntervalSeconds));
    }

    public TimeSpan RenewalInterval => _renewalInterval;

    public AcquireResult TryAcquire(string pipelineId, OwnerIdentity owner, LockKind kind)
    {
        var keyLock = _keyLocks.GetOrAdd(pipelineId, _ => new object());
        lock (keyLock)
        {
            if (_locks.TryGetValue(pipelineId, out var existing))
            {
                // Expired locks are treated as absent.
                if (existing.ExpiresUtc < DateTime.UtcNow)
                {
                    _locks.TryRemove(pipelineId, out _);
                    // Fall through to create a new lock.
                }
                else
                {
                    // Single-run rule: ANY active lock blocks a new trigger — including the
                    // caller's own and regardless of role. The only way past is Cancel
                    // (which stops the run and frees the lock), then trigger again.
                    return new AcquireResult.Conflict(existing);
                }
            }

            var now = DateTime.UtcNow;
            var newLock = new PipelineLock
            {
                PipelineId = pipelineId,
                Owner = owner,
                Kind = kind,
                Status = LockStatus.Active,
                AcquiredUtc = now,
                ExpiresUtc = now.AddSeconds(_heartbeatTimeoutSeconds),
                LastHeartbeatUtc = now,
                Token = Guid.NewGuid().ToString("N"),
            };
            _locks[pipelineId] = newLock;
            OnLockEvent?.Invoke(new LockEvent(LockEventKind.Acquired, newLock));
            return new AcquireResult.Success(newLock);
        }
    }

    public bool TryRelease(string pipelineId, string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var keyLock = _keyLocks.GetOrAdd(pipelineId, _ => new object());
        lock (keyLock)
        {
            if (!_locks.TryGetValue(pipelineId, out var existing))
                return false;

            // Release succeeds only when the caller presents the current lock's token.
            // A late release from a torn-down run (whose token no longer matches a newer
            // acquisition) is a no-op, so it can never free someone else's run.
            if (!string.Equals(existing.Token, token, StringComparison.Ordinal))
                return false;

            _locks.TryRemove(pipelineId, out _);
            var released = existing with { Status = LockStatus.Released };
            OnLockEvent?.Invoke(new LockEvent(LockEventKind.Released, released));
            return true;
        }
    }

    public PipelineLock? Get(string pipelineId)
    {
        if (!_locks.TryGetValue(pipelineId, out var existing))
            return null;

        // Don't return expired locks
        if (existing.ExpiresUtc < DateTime.UtcNow)
            return null;

        return existing;
    }

    public IReadOnlyList<PipelineLock> GetAll()
    {
        var now = DateTime.UtcNow;
        return _locks.Values.Where(l => l.ExpiresUtc >= now).ToList();
    }

    public void ForceRelease(string pipelineId)
    {
        var keyLock = _keyLocks.GetOrAdd(pipelineId, _ => new object());
        lock (keyLock)
        {
            if (!_locks.TryRemove(pipelineId, out var existing))
                return;

            var released = existing with { Status = LockStatus.ForceReleased };
            OnLockEvent?.Invoke(new LockEvent(LockEventKind.ForceReleased, released, existing.Owner));
        }
    }

    public bool Heartbeat(string pipelineId, OwnerIdentity owner)
    {
        var keyLock = _keyLocks.GetOrAdd(pipelineId, _ => new object());
        lock (keyLock)
        {
            if (!_locks.TryGetValue(pipelineId, out var existing))
                return false;

            if (existing.Owner != owner)
                return false;

            _locks[pipelineId] = existing.WithExtendedExpiry(
                DateTime.UtcNow.AddSeconds(_heartbeatTimeoutSeconds));
            return true;
        }
    }

    public bool TryRenew(string pipelineId, string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var keyLock = _keyLocks.GetOrAdd(pipelineId, _ => new object());
        lock (keyLock)
        {
            if (!_locks.TryGetValue(pipelineId, out var existing))
                return false;

            // Renew only when the caller presents the current lock's token, so a stray
            // renewal from a torn-down run can never revive a newer run's lock. Renewal
            // is silent (no event) — clients already render the lock and count elapsed
            // time from AcquiredUtc, which renewal does not change.
            if (!string.Equals(existing.Token, token, StringComparison.Ordinal))
                return false;

            _locks[pipelineId] = existing.WithExtendedExpiry(
                DateTime.UtcNow.AddSeconds(_heartbeatTimeoutSeconds));
            return true;
        }
    }

    public void RewriteOwners(OwnerIdentity newOwner)
    {
        foreach (var kvp in _locks.ToArray())
        {
            var keyLock = _keyLocks.GetOrAdd(kvp.Key, _ => new object());
            lock (keyLock)
            {
                if (_locks.TryGetValue(kvp.Key, out var current))
                {
                    var priorOwner = current.Owner;
                    var rewritten = current.WithOwner(newOwner);
                    _locks[kvp.Key] = rewritten;
                    OnLockEvent?.Invoke(new LockEvent(LockEventKind.Rewritten, rewritten, priorOwner));
                }
            }
        }
    }

    /// <summary>
    /// Called by LockExpirySweeper. Finds and expires all locks past their TTL.
    /// Returns the number of locks expired.
    /// </summary>
    public int ExpireStale()
    {
        var now = DateTime.UtcNow;
        var expired = 0;
        foreach (var kvp in _locks.ToArray())
        {
            if (kvp.Value.ExpiresUtc < now)
            {
                var keyLock = _keyLocks.GetOrAdd(kvp.Key, _ => new object());
                lock (keyLock)
                {
                    if (_locks.TryGetValue(kvp.Key, out var current) && current.ExpiresUtc < now)
                    {
                        _locks.TryRemove(kvp.Key, out _);
                        var expiredLock = current with { Status = LockStatus.Expired };
                        OnLockEvent?.Invoke(new LockEvent(LockEventKind.Expired, expiredLock, current.Owner));
                        expired++;
                    }
                }
            }
        }
        return expired;
    }
}
