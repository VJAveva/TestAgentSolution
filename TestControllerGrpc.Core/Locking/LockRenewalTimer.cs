using System;
using System.Threading;

namespace TestControllerGrpc.Locking;

/// <summary>
/// Keeps a live run's single-run lock alive by periodically renewing its TTL with the
/// per-acquisition token, so the expiry sweeper never reaps a lock mid-run (Impediment #1:
/// "a live run renews its lock periodically"). No-op when there is no registry or no token
/// (e.g. the standalone WebApi host, or hosts that own release themselves with an empty
/// context token). Dispose to stop renewing — a stray renewal that lands after release is a
/// harmless no-op because the lock is gone or carries a newer token.
/// </summary>
public sealed class LockRenewalTimer : IDisposable
{
    private readonly Timer? _timer;

    public LockRenewalTimer(ILockRegistry? registry, string pipelineId, string token, TimeSpan interval)
    {
        if (registry is null || string.IsNullOrEmpty(token) || interval <= TimeSpan.Zero)
            return;

        _timer = new Timer(
            _ =>
            {
                try { registry.TryRenew(pipelineId, token); }
                catch { /* best-effort; the sweeper + token model keep us safe */ }
            },
            null,
            interval,
            interval);
    }

    public void Dispose() => _timer?.Dispose();
}
