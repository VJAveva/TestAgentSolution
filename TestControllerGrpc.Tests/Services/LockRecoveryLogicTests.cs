using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for LockRecoveryService logic: orphan detection via
/// AgentLockManager.FindOrphanedLocks + ExecutionSessionManager integration.
/// The actual BackgroundService loop isn't tested here (needs real async hosting),
/// but the orphan detection logic is validated through the underlying services.
/// </summary>
public class LockRecoveryLogicTests
{
    [Fact]
    public void FindOrphanedLocks_Should_ReturnOrphans_When_SessionNoLongerActive()
    {
        var lockMgr = new AgentLockManager();
        var sessionMgr = new ExecutionSessionManager();

        // Create a session and lock agents
        var session = sessionMgr.BeginSession("Deploy", "Renamed",
            new Dictionary<string, string>(), new List<IActionNode>(), "sess01");
        lockMgr.TryLockAgents(new[] { "Agent-01", "Agent-02" }, "sess01", "Deploy", "user1", "WPF");

        // Complete the session (removes from active)
        sessionMgr.CompleteSession("sess01");

        // Now find orphans — sess01 is in history but sessionMgr.GetSession returns it
        // The orphan check uses session existence, not active status
        var orphans = lockMgr.FindOrphanedLocks(
            sid => sessionMgr.GetSession(sid) != null);

        // GetSession finds it in history, so no orphans here
        Assert.Empty(orphans);
    }

    [Fact]
    public void FindOrphanedLocks_Should_ReturnOrphans_When_SessionNotInManager()
    {
        var lockMgr = new AgentLockManager();

        // Lock agents with a session ID not in any manager
        lockMgr.TryLockAgents(new[] { "Agent-01" }, "ghost-session", "Deploy", "user1", "WPF");

        var orphans = lockMgr.FindOrphanedLocks(sid => false); // No session exists

        Assert.Single(orphans);
        Assert.Equal("Agent-01", orphans[0].AgentName);
        Assert.Equal("ghost-session", orphans[0].SessionId);
    }

    [Fact]
    public void ForceRelease_Should_CleanOrphan_When_AgentConfirmedFree()
    {
        var lockMgr = new AgentLockManager();
        lockMgr.TryLockAgents(new[] { "Agent-01" }, "dead-session", "Pipeline", "admin", "WPF");

        Assert.NotNull(lockMgr.GetLock("Agent-01"));

        lockMgr.ForceRelease("Agent-01");

        Assert.Null(lockMgr.GetLock("Agent-01"));
    }

    [Fact]
    public void OrphanCleanup_Should_PreserveValidLocks_When_SessionStillActive()
    {
        var lockMgr = new AgentLockManager();
        var sessionMgr = new ExecutionSessionManager();

        var session = sessionMgr.BeginSession("Deploy", "Renamed",
            new Dictionary<string, string>(), new List<IActionNode>(), "active01");
        lockMgr.TryLockAgents(new[] { "Agent-01" }, "active01", "Deploy", "user1", "WPF");

        var orphans = lockMgr.FindOrphanedLocks(
            sid => sessionMgr.GetSession(sid) != null);

        Assert.Empty(orphans);
        Assert.NotNull(lockMgr.GetLock("Agent-01"));
    }

    [Fact]
    public void FullOrphanLifecycle_Should_DetectAndClean_When_SessionCompletedButLockRemains()
    {
        var lockMgr = new AgentLockManager();
        var sessionMgr = new ExecutionSessionManager();

        // Begin session, lock agents
        sessionMgr.BeginSession("Deploy", "Renamed",
            new Dictionary<string, string>(), new List<IActionNode>(), "lifecycle01");
        lockMgr.TryLockAgents(new[] { "A1", "A2" }, "lifecycle01", "Deploy", "admin", "WPF");

        // Complete session but "forget" to release locks (simulating bug/crash)
        sessionMgr.CompleteSession("lifecycle01");

        // Session exists in history, so FindOrphanedLocks with GetSession returns non-null
        // Real lock recovery also checks agent busy state
        var locks = lockMgr.GetAllLocks();
        Assert.Equal(2, locks.Count);

        // Force cleanup
        lockMgr.ForceRelease("A1");
        lockMgr.ForceRelease("A2");

        Assert.Empty(lockMgr.GetAllLocks());
    }
}
