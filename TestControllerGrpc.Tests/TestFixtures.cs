using TestControllerGrpc.Services;
using static TestControllerGrpc.Services.AgentLockManager;

namespace TestControllerGrpc.Tests;

/// <summary>
/// Shared fixture utilities for creating synthetic sessions, locks, and agents
/// in test scenarios. Reduces boilerplate across test classes.
/// </summary>
public static class TestFixtures
{
    /// <summary>
    /// Creates a synthetic AgentLock for testing lock logic.
    /// </summary>
    public static AgentLock CreateLock(
        string agentName = "Agent1",
        string sessionId = "session-001",
        string source = "WebClient",
        string watchItemTag = "TestBuild")
    {
        return new AgentLock
        {
            AgentName = agentName,
            SessionId = sessionId,
            Source = source,
            WatchItemTag = watchItemTag,
            LockedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a collection of locks for multi-agent scenarios.
    /// </summary>
    public static List<AgentLock> CreateLocks(params string[] agentNames)
    {
        return agentNames.Select((name, i) => CreateLock(
            agentName: name,
            sessionId: $"session-{i:D3}")).ToList();
    }

    /// <summary>
    /// Creates a minimal execution session context for testing.
    /// </summary>
    public static TestControllerGrpc.Models.PipelineExecutionContext CreateExecutionContext(
        string sessionId = "test-session",
        string watchItemTag = "TestBuild")
    {
        return new TestControllerGrpc.Models.PipelineExecutionContext
        {
            SessionId = sessionId,
            WatchItemPath = watchItemTag,
        };
    }

    /// <summary>
    /// Generates a temp directory for test resources, auto-cleaned on dispose.
    /// </summary>
    public static TempDirectory CreateTempDir(string prefix = "TestFixture")
    {
        return new TempDirectory(prefix);
    }
}

/// <summary>
/// Disposable temp directory that auto-cleans on dispose.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string relativePath, string content = "")
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        var dir = System.IO.Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
