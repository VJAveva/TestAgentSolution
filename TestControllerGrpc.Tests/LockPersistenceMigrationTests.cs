using System.Text.Json;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests;

/// <summary>
/// CORE-006: Tests that lock persistence with schema versioning
/// correctly handles legacy and current formats.
/// </summary>
public class LockPersistenceMigrationTests : IDisposable
{
    private readonly string _tempPath;

    public LockPersistenceMigrationTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"lock_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        if (File.Exists(_tempPath + ".tmp")) File.Delete(_tempPath + ".tmp");
    }

    [Fact]
    public void RestoreFromLegacyFormat_LoadsLocks()
    {
        // Legacy format: bare array of lock objects (no envelope)
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                AgentName = "Agent-01",
                SessionId = "sess001",
                WatchItemTag = "Build1",
                UserId = "user1",
                Source = "WPF",
                LockedAtUtc = "2025-01-01T12:00:00Z",
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(_tempPath, legacyJson);

        var manager = new AgentLockManager(_tempPath);

        var locks = manager.GetAllLocks();
        Assert.Single(locks);
        Assert.Equal("Agent-01", locks[0].AgentName);
        Assert.Equal("sess001", locks[0].SessionId);
    }

    [Fact]
    public void RestoreFromVersionedFormat_LoadsLocks()
    {
        // New versioned format with envelope
        var envelopeJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Locks = new[]
            {
                new
                {
                    AgentName = "Agent-02",
                    SessionId = "sess002",
                    WatchItemTag = "Deploy",
                    UserId = "admin",
                    Source = "WebClient",
                    LockedAtUtc = "2025-06-15T10:30:00Z",
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(_tempPath, envelopeJson);

        var manager = new AgentLockManager(_tempPath);

        var locks = manager.GetAllLocks();
        Assert.Single(locks);
        Assert.Equal("Agent-02", locks[0].AgentName);
        Assert.Equal("sess002", locks[0].SessionId);
    }

    [Fact]
    public void PersistAndRestore_RoundTrips()
    {
        // Create manager, add a lock, then restore
        var manager1 = new AgentLockManager(_tempPath);
        var (locked, _) = manager1.TryLockAgents(
            ["TestAgent"], "session123", "BuildX", "testuser", "WPF");
        Assert.True(locked);

        // Wait for async persist to complete
        Thread.Sleep(200);

        // Load fresh instance from same file
        var manager2 = new AgentLockManager(_tempPath);
        var locks = manager2.GetAllLocks();
        Assert.Single(locks);
        Assert.Equal("TestAgent", locks[0].AgentName);
        Assert.Equal("session123", locks[0].SessionId);
    }

    [Fact]
    public void CorruptedFile_ClearsLocks()
    {
        File.WriteAllText(_tempPath, "not valid json {{{{");

        var manager = new AgentLockManager(_tempPath);

        Assert.Empty(manager.GetAllLocks());
    }

    [Fact]
    public void EmptyFile_NoLocks()
    {
        File.WriteAllText(_tempPath, "");

        var manager = new AgentLockManager(_tempPath);

        Assert.Empty(manager.GetAllLocks());
    }
}
