using System.Text.Json;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for the persistence layer added to ExecutionSessionManager:
/// PersistToDisk, RestoreFromDisk, GetPersistedHistory, and the
/// PersistedSession/PersistedActionResult DTOs.
/// </summary>
public class SessionPersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _persistPath;

    public SessionPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionPersistTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _persistPath = Path.Combine(_tempDir, "session-snapshots.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ExecutionSessionManager CreatePersistentManager()
        => new(new EventAggregator(), _persistPath);

    private ExecutionSessionManager CreateNonPersistentManager()
        => new();

    /// <summary>Polls for the persist file to appear (or be updated) rather than using a fixed sleep.</summary>
    private bool WaitForFile(int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(_persistPath)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Waits for the file to be written after deletion.</summary>
    private bool WaitForFileAfterDelete(int timeoutMs = 5000)
    {
        if (File.Exists(_persistPath)) File.Delete(_persistPath);
        return WaitForFile(timeoutMs);
    }

    private ExecutionSession BeginTestSession(
        ExecutionSessionManager mgr, string tag = "TestItem",
        string? sessionId = null, string userId = "", string source = "")
    {
        var session = mgr.BeginSession(tag, "Renamed",
            new Dictionary<string, string>
            {
                ["_BuildNumber"] = "2026.05.14.1",
                ["Param1"] = "Value1",
            },
            new List<IActionNode> { new ActionConfig { Command = "echo test" } },
            sessionId);
        if (!string.IsNullOrEmpty(userId)) session.UserId = userId;
        if (!string.IsNullOrEmpty(source)) session.Source = source;
        return session;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PersistToDisk
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void PersistToDisk_Should_CreateFile_When_SessionCompleted()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr, "Deploy.WebApi");
        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "Install",
            ActionType = "RunRemoteCommand",
            AgentName = "Agent-01",
            Outcome = ActionOutcome.Success,
            ExitCode = 0,
            Duration = TimeSpan.FromSeconds(30),
        });
        mgr.CompleteSession(session.SessionId);

        // PersistToDisk runs on ThreadPool — poll for file
        WaitForFile();

        Assert.True(File.Exists(_persistPath), "session-snapshots.json should be created");
    }

    [Fact]
    public void PersistToDisk_Should_IncludeActiveSessions_When_Running()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr, "ActivePipeline", sessionId: "active01");
        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "Step1",
            Outcome = ActionOutcome.Success,
        });

        // BeginSession triggers PersistToDisk
        WaitForFile();

        Assert.True(File.Exists(_persistPath));
        var json = File.ReadAllText(_persistPath);
        var entries = JsonSerializer.Deserialize<ExecutionSessionManager.PersistedSession[]>(json);
        Assert.NotNull(entries);
        Assert.Contains(entries, e => e.SessionId == "active01");
        Assert.Contains(entries, e => e.State == "Running");
    }

    [Fact]
    public void PersistToDisk_Should_UseAtomicWrite_When_Called()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr);
        mgr.CompleteSession(session.SessionId);

        WaitForFile();

        // No .tmp file should remain after atomic write
        Assert.False(File.Exists(_persistPath + ".tmp"),
            "Temp file should be cleaned up after atomic move");
        Assert.True(File.Exists(_persistPath));
    }

    [Fact]
    public void PersistToDisk_Should_NotThrow_When_NoPersistPath()
    {
        var mgr = CreateNonPersistentManager();
        var session = BeginTestSession(mgr);

        // Should not throw — persist is a no-op
        mgr.CompleteSession(session.SessionId);

        Assert.False(File.Exists(_persistPath));
    }

    [Fact]
    public void PersistToDisk_Should_SerializeAllFields_When_FullSession()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr, "FullTest",
            sessionId: "full01", userId: "dev1", source: "WebClient");
        session.LockedAgents = new[] { "Agent-01", "Agent-02" };

        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "Install",
            ActionType = "RunRemoteCommand",
            AgentName = "Agent-01",
            Command = @"\\server\install.cmd",
            Outcome = ActionOutcome.Success,
            ExitCode = 0,
            ErrorMessage = "",
            Duration = TimeSpan.FromSeconds(45),
        });
        mgr.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "RunTests",
            ActionType = "RunRemoteCommand",
            AgentName = "Agent-02",
            Command = "dotnet test",
            Outcome = ActionOutcome.Failed,
            ExitCode = 1,
            ErrorMessage = "3 tests failed",
            Duration = TimeSpan.FromMinutes(5),
        });

        mgr.CompleteSession(session.SessionId);
        WaitForFile();

        var json = File.ReadAllText(_persistPath);
        var entries = JsonSerializer.Deserialize<ExecutionSessionManager.PersistedSession[]>(json);
        Assert.NotNull(entries);
        Assert.Single(entries);

        var entry = entries[0];
        Assert.Equal("full01", entry.SessionId);
        Assert.Equal("FullTest", entry.WatchItemTag);
        Assert.Equal("dev1", entry.UserId);
        Assert.Equal("WebClient", entry.Source);
        Assert.Equal("PartialFailure", entry.State);
        Assert.Equal(new[] { "Agent-01", "Agent-02" }, entry.LockedAgents);
        Assert.NotNull(entry.CompletedUtc);
        Assert.Equal(2, entry.ActionResults.Length);

        var install = entry.ActionResults.First(a => a.ActionTag == "Install");
        Assert.Equal("RunRemoteCommand", install.ActionType);
        Assert.Equal("Agent-01", install.AgentName);
        Assert.Equal("Success", install.Outcome);
        Assert.Equal(0, install.ExitCode);

        var test = entry.ActionResults.First(a => a.ActionTag == "RunTests");
        Assert.Equal("Failed", test.Outcome);
        Assert.Equal(1, test.ExitCode);
        Assert.Equal("3 tests failed", test.ErrorMessage);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RestoreFromDisk
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RestoreFromDisk_Should_LoadHistory_When_FileExists()
    {
        // Persist a completed session
        var mgr1 = CreatePersistentManager();
        var session = BeginTestSession(mgr1, "Restore.Test", sessionId: "restore01");
        mgr1.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "Build",
            Outcome = ActionOutcome.Success,
        });
        mgr1.CompleteSession(session.SessionId);
        WaitForFile();

        // Create new manager from same persist path — should restore
        var mgr2 = CreatePersistentManager();

        var restored = mgr2.GetSession("restore01");
        Assert.NotNull(restored);
        Assert.Equal("Restore.Test", restored.WatchItemTag);
        Assert.Equal(SessionState.Completed, restored.State);
    }

    [Fact]
    public void RestoreFromDisk_Should_MarkRunningAsFailed_When_Restored()
    {
        // Write a session that appears to be "Running" (simulating crash)
        var persisted = new[]
        {
            new ExecutionSessionManager.PersistedSession
            {
                SessionId = "crashed01",
                WatchItemTag = "Nightly.Suite",
                State = "Running",
                UserId = "scheduler",
                Source = "WebClient",
                ActionResults = new[]
                {
                    new ExecutionSessionManager.PersistedActionResult
                    {
                        ActionTag = "Install",
                        Outcome = "Success",
                        AgentName = "Agent-01",
                    },
                    new ExecutionSessionManager.PersistedActionResult
                    {
                        ActionTag = "RunTests",
                        Outcome = "Unknown", // Was running when crash happened
                        AgentName = "Agent-02",
                    },
                },
            }
        };

        File.WriteAllText(_persistPath,
            JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));

        var mgr = CreatePersistentManager();
        var session = mgr.GetSession("crashed01");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Failed, session.State); // Running → Failed
        Assert.Equal("Nightly.Suite", session.WatchItemTag);
    }

    [Fact]
    public void RestoreFromDisk_Should_RestoreActionResults_When_Persisted()
    {
        var persisted = new[]
        {
            new ExecutionSessionManager.PersistedSession
            {
                SessionId = "results01",
                WatchItemTag = "Test",
                State = "Failed",
                ActionResults = new[]
                {
                    new ExecutionSessionManager.PersistedActionResult
                    {
                        ActionTag = "Install",
                        ActionType = "RunRemoteCommand",
                        AgentName = "Agent-01",
                        Command = "install.cmd",
                        Outcome = "Success",
                        ExitCode = 0,
                        Duration = "00:00:30",
                        StartedUtc = DateTime.UtcNow.AddMinutes(-5).ToString("o"),
                    },
                    new ExecutionSessionManager.PersistedActionResult
                    {
                        ActionTag = "RunTests",
                        ActionType = "RunRemoteCommand",
                        AgentName = "Agent-01",
                        Command = "dotnet test",
                        Outcome = "Failed",
                        ExitCode = 1,
                        ErrorMessage = "Tests failed",
                        Duration = "00:05:00",
                        StartedUtc = DateTime.UtcNow.AddMinutes(-4).ToString("o"),
                    },
                },
            }
        };

        File.WriteAllText(_persistPath,
            JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));

        var mgr = CreatePersistentManager();
        var session = mgr.GetSession("results01");

        Assert.NotNull(session);
        Assert.Equal(2, session.TotalActions);
        Assert.Equal(1, session.SucceededCount);
        Assert.Equal(1, session.FailedCount);

        // Agent summaries should be populated too
        var summaries = session.GetAgentSummaries();
        Assert.Single(summaries);
        Assert.Equal("Agent-01", summaries[0].AgentName);
    }

    [Fact]
    public void RestoreFromDisk_Should_DoNothing_When_FileNotExists()
    {
        // Ensure file doesn't exist
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        var mgr = CreatePersistentManager();

        Assert.Equal(0, mgr.GetHistory(50).Count);
        Assert.False(mgr.HasAnyActiveExecution);
    }

    [Fact]
    public void RestoreFromDisk_Should_ClearAndContinue_When_FileCorrupt()
    {
        File.WriteAllText(_persistPath, "{ invalid json garbage @#$% }");

        // Should not throw
        var mgr = CreatePersistentManager();

        Assert.Equal(0, mgr.GetHistory(50).Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GetPersistedHistory
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GetPersistedHistory_Should_ReturnEntries_When_FileExists()
    {
        var mgr = CreatePersistentManager();
        var s1 = BeginTestSession(mgr, "Item1", sessionId: "gh01");
        mgr.CompleteSession(s1.SessionId);
        var s2 = BeginTestSession(mgr, "Item2", sessionId: "gh02");
        mgr.CompleteSession(s2.SessionId);

        WaitForFile();

        var persisted = mgr.GetPersistedHistory();
        Assert.True(persisted.Count >= 2);
        Assert.Contains(persisted, p => p.SessionId == "gh01");
        Assert.Contains(persisted, p => p.SessionId == "gh02");
    }

    [Fact]
    public void GetPersistedHistory_Should_ReturnEmpty_When_NoFile()
    {
        if (File.Exists(_persistPath)) File.Delete(_persistPath);
        var mgr = CreatePersistentManager();

        // File was created by another BeginSession, so delete it
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        var result = mgr.GetPersistedHistory();
        Assert.Empty(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Persist triggers on lifecycle methods
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CompleteSession_Should_PersistToDisk_When_Called()
    {
        var mgr = CreatePersistentManager();
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        var session = BeginTestSession(mgr, sessionId: "persist-complete");
        WaitForFile();
        // Delete file created by BeginSession
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        mgr.CompleteSession(session.SessionId);
        WaitForFile();

        Assert.True(File.Exists(_persistPath));
    }

    [Fact]
    public void CancelSession_Should_PersistToDisk_When_Called()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr, sessionId: "persist-cancel");
        WaitForFile();
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        mgr.CancelSession(session.SessionId);
        WaitForFile();

        Assert.True(File.Exists(_persistPath));
    }

    [Fact]
    public void CancelAll_Should_PersistToDisk_When_SessionsExist()
    {
        var mgr = CreatePersistentManager();
        BeginTestSession(mgr, "A", sessionId: "ca01");
        BeginTestSession(mgr, "B", sessionId: "ca02");
        WaitForFile();
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        mgr.CancelAll();
        WaitForFile();

        Assert.True(File.Exists(_persistPath));
    }

    // ═══════════════════════════════════════════════════════════════════
    // PersistedSession DTO round-trip
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void PersistedSession_Should_RoundTrip_When_Serialized()
    {
        var original = new ExecutionSessionManager.PersistedSession
        {
            SessionId = "rt01",
            WatchItemTag = "Deploy.All",
            EventType = "Renamed",
            State = "Completed",
            UserId = "admin",
            Source = "WPF",
            LockedAgents = new[] { "A1", "A2" },
            StartedUtc = DateTime.UtcNow.ToString("o"),
            CompletedUtc = DateTime.UtcNow.AddMinutes(5).ToString("o"),
            ResolvedParameters = new() { ["Build"] = "1.0" },
            ActionResults = new[]
            {
                new ExecutionSessionManager.PersistedActionResult
                {
                    ActionTag = "Install",
                    ActionType = "RunRemoteCommand",
                    AgentName = "A1",
                    Command = "install.cmd",
                    Outcome = "Success",
                    ExitCode = 0,
                    Duration = "00:00:30",
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    Sequence = 1,
                },
            },
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ExecutionSessionManager.PersistedSession>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("rt01", deserialized.SessionId);
        Assert.Equal("Deploy.All", deserialized.WatchItemTag);
        Assert.Equal("admin", deserialized.UserId);
        Assert.Single(deserialized.ActionResults);
        Assert.Equal("Install", deserialized.ActionResults[0].ActionTag);
    }

    // ═══════════════════════════════════════════════════════════════════
    // RecordResult throttled persist
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void RecordResult_Should_EventuallyPersist_When_Called()
    {
        var mgr = CreatePersistentManager();
        var session = BeginTestSession(mgr, sessionId: "throttle01");
        WaitForFile();
        if (File.Exists(_persistPath)) File.Delete(_persistPath);

        // Record many results rapidly — only some will trigger persist
        for (var i = 0; i < 10; i++)
        {
            mgr.RecordResult(session.SessionId, new ActionExecutionResult
            {
                ActionTag = $"Step{i}",
                Outcome = ActionOutcome.Success,
            });
        }

        mgr.CompleteSession(session.SessionId);
        WaitForFile();

        Assert.True(File.Exists(_persistPath));
        var json = File.ReadAllText(_persistPath);
        var entries = JsonSerializer.Deserialize<ExecutionSessionManager.PersistedSession[]>(json);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries);
        Assert.Equal(10, entry.ActionResults.Length);
    }
}
