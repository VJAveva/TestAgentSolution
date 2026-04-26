using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Background service that reconciles persisted lock state with actual
/// agent status on startup, then periodically detects and cleans orphaned locks.
///
/// Startup flow:
///   1. Waits for services to initialize (10s)
///   2. Reads persisted locks (already loaded by AgentLockManager constructor)
///   3. Pings every registered agent to check busy/free state
///   4. Removes stale locks (agent free but lock exists)
///   5. Broadcasts corrected lock state
///
/// Periodic flow (every 5 minutes):
///   1. Finds locks with no matching active session
///   2. Pings those agents to confirm they're free
///   3. Removes confirmed orphans
/// </summary>
public sealed class LockRecoveryService : BackgroundService
{
    private readonly AgentLockManager _lockManager;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<LockRecoveryService> _logger;

    private static readonly TimeSpan OrphanCheckInterval = TimeSpan.FromMinutes(5);

    public LockRecoveryService(
        AgentLockManager lockManager,
        IAgentGrpcDispatcher dispatcher,
        ExecutionSessionManager sessionManager,
        IEventAggregator eventAggregator,
        ILogger<LockRecoveryService> logger)
    {
        _lockManager = lockManager;
        _dispatcher = dispatcher;
        _sessionManager = sessionManager;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for services to initialize
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        await RecoverLocksFromAgentState(ct);

        // Periodic orphan detection
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(OrphanCheckInterval, ct); }
            catch (OperationCanceledException) { break; }

            await DetectAndCleanOrphans(ct);
        }
    }

    /// <summary>
    /// Startup recovery: validates persisted locks against actual agent state.
    /// Removes stale locks where the agent finished while controller was down.
    /// </summary>
    private async Task RecoverLocksFromAgentState(CancellationToken ct)
    {
        var persistedLocks = _lockManager.GetAllLocks();
        if (persistedLocks.Count == 0)
        {
            _logger.LogInformation("[LockRecovery] No persisted locks to validate");
            return;
        }

        _logger.LogInformation(
            "[LockRecovery] Validating {Count} persisted lock(s) against agent state",
            persistedLocks.Count);

        int validated = 0, staleRemoved = 0;

        foreach (var agentLock in persistedLocks)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var isBusy = await IsAgentBusy(agentLock.AgentName, ct);

                if (isBusy)
                {
                    _logger.LogInformation(
                        "[LockRecovery] {Agent}: BUSY — lock validated (session: {Session}, pipeline: {Pipeline})",
                        agentLock.AgentName, agentLock.SessionId, agentLock.WatchItemTag);
                    validated++;
                }
                else
                {
                    _logger.LogWarning(
                        "[LockRecovery] {Agent}: FREE but lock exists — removing stale lock (was: {Pipeline} by {User})",
                        agentLock.AgentName, agentLock.WatchItemTag, agentLock.UserId);
                    _lockManager.ForceRelease(agentLock.AgentName);
                    staleRemoved++;
                }
            }
            catch (Exception ex)
            {
                // Agent unreachable — keep lock (conservative: assume still busy)
                _logger.LogWarning(
                    "[LockRecovery] {Agent}: UNREACHABLE ({Error}) — keeping lock",
                    agentLock.AgentName, ex.Message);
                validated++;
            }
        }

        _logger.LogInformation(
            "[LockRecovery] Complete: {Validated} validated, {StaleRemoved} stale removed",
            validated, staleRemoved);

        if (staleRemoved > 0)
            BroadcastLockState("Lock recovery after restart");
    }

    /// <summary>
    /// Periodic: finds locks with no matching active session and removes
    /// them after confirming the agent is free.
    /// </summary>
    private async Task DetectAndCleanOrphans(CancellationToken ct)
    {
        var orphans = _lockManager.FindOrphanedLocks(
            sessionId => _sessionManager.GetSession(sessionId) != null);

        if (orphans.Count == 0) return;

        _logger.LogWarning("[LockRecovery] Orphan detection: {Count} orphaned lock(s)", orphans.Count);
        int cleaned = 0;

        foreach (var orphan in orphans)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (await IsAgentBusy(orphan.AgentName, ct))
                {
                    _logger.LogWarning(
                        "[LockRecovery] Orphan {Agent}: still BUSY — keeping lock",
                        orphan.AgentName);
                    continue;
                }
            }
            catch
            {
                continue; // Unreachable — keep lock
            }

            _logger.LogWarning(
                "[LockRecovery] Orphan {Agent}: FREE and session {Session} gone — removing",
                orphan.AgentName, orphan.SessionId);
            _lockManager.ForceRelease(orphan.AgentName);
            cleaned++;
        }

        if (cleaned > 0)
            BroadcastLockState("Orphan cleanup");
    }

    private async Task<bool> IsAgentBusy(string agentName, CancellationToken ct)
    {
        var (snapshot, error) = await _dispatcher.TestConnectionAsync(agentName, ct);
        if (snapshot == null)
            throw new InvalidOperationException(error ?? "Agent unreachable");

        var state = snapshot.State.ToString();
        return state.Contains("Busy", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Executing", StringComparison.OrdinalIgnoreCase);
    }

    private void BroadcastLockState(string reason)
    {
        _eventAggregator.Publish(new AgentLocksChangedEvent
        {
            Locks = _lockManager.GetAllLocks()
                .Select(l => new AgentLockInfo
                {
                    AgentName = l.AgentName,
                    SessionId = l.SessionId,
                    WatchItemTag = l.WatchItemTag,
                    UserId = l.UserId,
                    Source = l.Source,
                    LockedAtUtc = l.LockedAtUtc,
                }).ToList(),
            Reason = reason,
        });
    }
}
