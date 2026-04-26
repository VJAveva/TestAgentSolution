using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for AgentLockManager: atomic locking, session release,
/// force-release, version counter, persistence, orphan detection,
/// and session lock validation.
/// </summary>
public class AgentLockManagerTests : IDisposable
{
    private readonly string _tempDir;

    public AgentLockManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LockTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private AgentLockManager Create(bool persist = false) =>
        persist ? new AgentLockManager(Path.Combine(_tempDir, "locks.json")) : new AgentLockManager();

    // ?????????????????????????????????????????????????????????????????
    // TryLockAgents
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void TryLockAgents_Should_Succeed_When_AllAgentsFree()
    {
        var mgr = Create();

        var (success, conflicts) = mgr.TryLockAgents(
            ["agent1", "agent2"], "S1", "Set1", "Vinod", "WPF");

        Assert.True(success);
        Assert.Empty(conflicts);
        Assert.Equal(2, mgr.GetAllLocks().Count);
    }

    [Fact]
    public void TryLockAgents_Should_Fail_When_AnyAgentLocked()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");

        var (success, conflicts) = mgr.TryLockAgents(
            ["agent1", "agent2"], "S2", "Set2", "Ravi", "WebClient");

        Assert.False(success);
        Assert.Single(conflicts);
        Assert.Equal("agent1", conflicts[0].AgentName);
        Assert.Equal("S1", conflicts[0].SessionId);
    }

    [Fact]
    public void TryLockAgents_Should_NotLockAny_When_ConflictExists()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");

        mgr.TryLockAgents(["agent1", "agent2"], "S2", "Set2", "Ravi", "WebClient");

        // agent2 should NOT be locked (atomic: all-or-nothing)
        Assert.Null(mgr.GetLock("agent2"));
        Assert.Equal(1, mgr.GetAllLocks().Count);
    }

    [Fact]
    public void TryLockAgents_Should_Succeed_When_EmptyAgentList()
    {
        var mgr = Create();

        var (success, conflicts) = mgr.TryLockAgents(
            Array.Empty<string>(), "S1", "Set1", "Vinod", "WPF");

        Assert.True(success);
        Assert.Empty(conflicts);
        Assert.Empty(mgr.GetAllLocks());
    }

    [Fact]
    public void TryLockAgents_Should_Succeed_When_SameSessionRelocks()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");

        // Same session re-locking is idempotent
        var (success, _) = mgr.TryLockAgents(
            ["agent1"], "S1", "Set1", "Vinod", "WPF");

        Assert.True(success);
    }

    [Fact]
    public void TryLockAgents_Should_BeCaseInsensitive_When_AgentNamesVary()
    {
        var mgr = Create();
        mgr.TryLockAgents(["Agent1"], "S1", "Set1", "Vinod", "WPF");

        var (success, conflicts) = mgr.TryLockAgents(
            ["AGENT1"], "S2", "Set2", "Ravi", "WebClient");

        Assert.False(success);
        Assert.Single(conflicts);
    }

    // ?????????????????????????????????????????????????????????????????
    // ReleaseSession
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ReleaseSession_Should_ReleaseAllAgents_When_SessionMatches()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1", "agent2"], "S1", "Set1", "Vinod", "WPF");

        var released = mgr.ReleaseSession("S1");

        Assert.Equal(2, released);
        Assert.Empty(mgr.GetAllLocks());
    }

    [Fact]
    public void ReleaseSession_Should_NotAffectOtherSessions_When_Released()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");
        mgr.TryLockAgents(["agent2"], "S2", "Set2", "Ravi", "WebClient");

        mgr.ReleaseSession("S1");

        Assert.Null(mgr.GetLock("agent1"));
        Assert.NotNull(mgr.GetLock("agent2"));
        Assert.Single(mgr.GetAllLocks());
    }

    [Fact]
    public void ReleaseSession_Should_ReturnZero_When_SessionNotFound()
    {
        var mgr = Create();
        Assert.Equal(0, mgr.ReleaseSession("nonexistent"));
    }

    [Fact]
    public void ReleaseSession_Should_BeCaseInsensitive_When_SessionIdVaries()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "s1", "Set1", "Vinod", "WPF");

        var released = mgr.ReleaseSession("S1");

        Assert.Equal(1, released);
        Assert.Empty(mgr.GetAllLocks());
    }

    // ?????????????????????????????????????????????????????????????????
    // ForceRelease
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ForceRelease_Should_RemoveSingleAgent_When_Locked()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1", "agent2"], "S1", "Set1", "Vinod", "WPF");

        var result = mgr.ForceRelease("agent1");

        Assert.True(result);
        Assert.Null(mgr.GetLock("agent1"));
        Assert.NotNull(mgr.GetLock("agent2"));
    }

    [Fact]
    public void ForceRelease_Should_ReturnFalse_When_NotLocked()
    {
        var mgr = Create();
        Assert.False(mgr.ForceRelease("noagent"));
    }

    // ?????????????????????????????????????????????????????????????????
    // ForceReleaseAll
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ForceReleaseAll_Should_ClearAll_When_LocksExist()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1", "a2"], "S1", "Set1", "Vinod", "WPF");
        mgr.TryLockAgents(["a3"], "S2", "Set2", "Ravi", "WebClient");

        var count = mgr.ForceReleaseAll();

        Assert.Equal(3, count);
        Assert.Empty(mgr.GetAllLocks());
    }

    [Fact]
    public void ForceReleaseAll_Should_ReturnZero_When_Empty()
    {
        var mgr = Create();
        Assert.Equal(0, mgr.ForceReleaseAll());
    }

    // ?????????????????????????????????????????????????????????????????
    // GetLock / GetSessionAgents / CheckAvailability
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void GetLock_Should_ReturnLock_When_Exists()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");

        var l = mgr.GetLock("agent1");

        Assert.NotNull(l);
        Assert.Equal("S1", l.SessionId);
        Assert.Equal("Set1", l.WatchItemTag);
        Assert.Equal("Vinod", l.UserId);
        Assert.Equal("WPF", l.Source);
    }

    [Fact]
    public void GetLock_Should_ReturnNull_When_NotExists()
    {
        var mgr = Create();
        Assert.Null(mgr.GetLock("nope"));
    }

    [Fact]
    public void GetSessionAgents_Should_ReturnLockedAgents_When_SessionExists()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1", "a2"], "S1", "Set1", "Vinod", "WPF");
        mgr.TryLockAgents(["a3"], "S2", "Set2", "Ravi", "WebClient");

        var agents = mgr.GetSessionAgents("S1");

        Assert.Equal(2, agents.Count);
        Assert.Contains("a1", agents, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("a2", agents, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckAvailability_Should_ReturnEmpty_When_AllFree()
    {
        var mgr = Create();
        var conflicts = mgr.CheckAvailability(["agent1", "agent2"]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void CheckAvailability_Should_ReturnConflicts_When_SomeLocked()
    {
        var mgr = Create();
        mgr.TryLockAgents(["agent1"], "S1", "Set1", "Vinod", "WPF");

        var conflicts = mgr.CheckAvailability(["agent1", "agent2"]);

        Assert.Single(conflicts);
        Assert.Equal("agent1", conflicts[0].AgentName);
    }

    // ?????????????????????????????????????????????????????????????????
    // Version counter
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void Version_Should_StartAtZero_When_Created()
    {
        var mgr = Create();
        Assert.Equal(0, mgr.Version);
    }

    [Fact]
    public void Version_Should_Increment_When_LockAcquired()
    {
        var mgr = Create();

        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");

        Assert.Equal(1, mgr.Version);
    }

    [Fact]
    public void Version_Should_Increment_When_SessionReleased()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        var v1 = mgr.Version;

        mgr.ReleaseSession("S1");

        Assert.True(mgr.Version > v1);
    }

    [Fact]
    public void Version_Should_NotIncrement_When_LockFails()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        var v = mgr.Version;

        mgr.TryLockAgents(["a1"], "S2", "Set2", "Ravi", "WebClient");

        Assert.Equal(v, mgr.Version);
    }

    [Fact]
    public void Version_Should_NotIncrement_When_ReleaseSessionFindsNothing()
    {
        var mgr = Create();
        var v = mgr.Version;
        mgr.ReleaseSession("nope");
        Assert.Equal(v, mgr.Version);
    }

    [Fact]
    public void Version_Should_Increment_When_ForceReleased()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        var v = mgr.Version;

        mgr.ForceRelease("a1");

        Assert.True(mgr.Version > v);
    }

    [Fact]
    public void Version_Should_Increment_When_ForceReleaseAll()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        var v = mgr.Version;

        mgr.ForceReleaseAll();

        Assert.True(mgr.Version > v);
    }

    [Fact]
    public void Version_Should_NotIncrement_When_ForceReleaseAllEmpty()
    {
        var mgr = Create();
        mgr.ForceReleaseAll();
        Assert.Equal(0, mgr.Version);
    }

    // ?????????????????????????????????????????????????????????????????
    // FindOrphanedLocks
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void FindOrphanedLocks_Should_ReturnOrphans_When_SessionInactive()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        mgr.TryLockAgents(["a2"], "S2", "Set2", "Ravi", "WebClient");

        // S1 is "active", S2 is not
        var orphans = mgr.FindOrphanedLocks(sid => sid == "S1");

        Assert.Single(orphans);
        Assert.Equal("S2", orphans[0].SessionId);
    }

    [Fact]
    public void FindOrphanedLocks_Should_ReturnEmpty_When_AllSessionsActive()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");

        var orphans = mgr.FindOrphanedLocks(_ => true);

        Assert.Empty(orphans);
    }

    // ?????????????????????????????????????????????????????????????????
    // ValidateSessionLocks
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ValidateSessionLocks_Should_ReturnEmpty_When_AllIntact()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1", "a2"], "S1", "Set1", "Vinod", "WPF");

        var missing = mgr.ValidateSessionLocks("S1", ["a1", "a2"]);

        Assert.Empty(missing);
    }

    [Fact]
    public void ValidateSessionLocks_Should_ReturnMissing_When_LockRemoved()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1", "a2"], "S1", "Set1", "Vinod", "WPF");
        mgr.ForceRelease("a1");

        var missing = mgr.ValidateSessionLocks("S1", ["a1", "a2"]);

        Assert.Single(missing);
        Assert.Equal("a1", missing[0]);
    }

    [Fact]
    public void ValidateSessionLocks_Should_ReturnMissing_When_StolenByOtherSession()
    {
        var mgr = Create();
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        mgr.ForceRelease("a1");
        mgr.TryLockAgents(["a1"], "S2", "Set2", "Ravi", "WebClient");

        var missing = mgr.ValidateSessionLocks("S1", ["a1"]);

        Assert.Single(missing);
    }

    // ?????????????????????????????????????????????????????????????????
    // Persistence
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void Persistence_Should_RestoreLocks_When_Recreated()
    {
        var path = Path.Combine(_tempDir, "persist_test.json");

        var mgr1 = new AgentLockManager(path);
        mgr1.TryLockAgents(["agent1", "agent2"], "S1", "Set1", "Vinod", "WPF");

        // Wait for async persist to complete
        Thread.Sleep(500);

        Assert.True(File.Exists(path), "Lock file should exist");

        // Recreate — should restore
        var mgr2 = new AgentLockManager(path);
        var locks = mgr2.GetAllLocks();

        Assert.Equal(2, locks.Count);
        Assert.Contains(locks, l => l.AgentName == "agent1" && l.SessionId == "S1");
        Assert.Contains(locks, l => l.AgentName == "agent2" && l.SessionId == "S1");
    }

    [Fact]
    public void Persistence_Should_ClearFile_When_AllReleased()
    {
        var path = Path.Combine(_tempDir, "persist_clear.json");

        var mgr = new AgentLockManager(path);
        mgr.TryLockAgents(["a1"], "S1", "Set1", "Vinod", "WPF");
        Thread.Sleep(500);

        mgr.ForceReleaseAll();
        Thread.Sleep(500);

        var mgr2 = new AgentLockManager(path);
        Assert.Empty(mgr2.GetAllLocks());
    }

    [Fact]
    public void Persistence_Should_HandleCorruptFile_When_Restoring()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "THIS IS NOT JSON!!!");

        var mgr = new AgentLockManager(path);

        Assert.Empty(mgr.GetAllLocks());
    }

    [Fact]
    public void PersistPath_Should_ReturnNull_When_NotConfigured()
    {
        var mgr = new AgentLockManager();
        Assert.Null(mgr.PersistPath);
    }

    [Fact]
    public void PersistPath_Should_ReturnPath_When_Configured()
    {
        var path = Path.Combine(_tempDir, "test.json");
        var mgr = new AgentLockManager(path);
        Assert.Equal(path, mgr.PersistPath);
    }

    // ?????????????????????????????????????????????????????????????????
    // Concurrency
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void TryLockAgents_Should_BeAtomic_When_ConcurrentCalls()
    {
        var mgr = Create();
        int successCount = 0;

        // 10 threads all try to lock the same agent
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            var (ok, _) = mgr.TryLockAgents(
                ["shared-agent"], $"S{i}", $"Set{i}", $"User{i}", "WPF");
            if (ok) Interlocked.Increment(ref successCount);
        })).ToArray();

        Task.WaitAll(tasks);

        // Exactly one should win
        Assert.Equal(1, successCount);
        Assert.Single(mgr.GetAllLocks());
    }
}
