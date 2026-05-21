using System.Collections.Concurrent;
using System.Text.Json;

namespace TestControllerGrpc.Services;

/// <summary>
/// Manages agent-level locks for pipeline execution isolation.
///
/// RULES:
///   - Lock granularity is per-AGENT, not per-WatchItem
///   - A pipeline can only start if ALL its required agents are free
///   - Lock acquisition is atomic: all agents or none
///   - Locks are held for the entire session duration
///   - Only the session owner or WPF admin can release locks
///   - Lock state is persisted to disk and restored on startup
///
/// DESIGN DECISION � SESSION-LEVEL LOCKING:
///   Locks are released at the SESSION level, never at the action level.
///   Even when one agent finishes early in a parallel pipeline, it stays
///   locked until the entire session completes. This prevents another
///   pipeline from modifying an agent's state (e.g., installing a different
///   build) while sibling groups still expect a consistent environment.
///   To release agents independently, use separate WatchItems/sessions.
///
/// THREADING: All public methods are thread-safe.
/// TryLockAgents uses a global lock for atomicity.
/// </summary>
public sealed class AgentLockManager
{
    private readonly ConcurrentDictionary<string, AgentLock> _locks
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _atomicLock = new();
    private readonly string? _persistPath;
    private readonly object _persistLock = new();
    private long _version;

    /// <summary>
    /// Represents a lock held on a single agent by a session.
    /// </summary>
    public record AgentLock
    {
        public string AgentName { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string WatchItemTag { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Source { get; init; } = "";  // "WebClient" | "WPF" | "Recovered"
        public DateTime LockedAtUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates an AgentLockManager with optional file persistence.
    /// If persistPath is provided, locks are saved to disk on every
    /// change and restored on construction.
    /// </summary>
    public AgentLockManager(string? persistPath = null)
    {
        _persistPath = persistPath;
        if (!string.IsNullOrEmpty(_persistPath))
            RestoreFromDisk();
    }

    /// <summary>
    /// Monotonically increasing version number, incremented on every lock state change.
    /// Clients pass their last-seen version on trigger to detect stale state.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Attempts to lock ALL required agents atomically.
    /// If ANY agent is already locked by a different session,
    /// returns (false, list-of-conflicts). No agents are locked.
    /// If ALL agents are free, locks them all and returns (true, empty).
    /// </summary>
    public (bool Success, IReadOnlyList<AgentLock> Conflicts)
        TryLockAgents(
            IReadOnlyList<string> agentNames,
            string sessionId,
            string watchItemTag,
            string userId,
            string source)
    {
        if (agentNames.Count == 0)
            return (true, Array.Empty<AgentLock>());

        lock (_atomicLock)
        {
            var conflicts = new List<AgentLock>();
            foreach (var agent in agentNames)
            {
                if (_locks.TryGetValue(agent, out var existing) &&
                    existing.SessionId != sessionId)
                {
                    conflicts.Add(existing);
                }
            }

            if (conflicts.Count > 0)
                return (false, conflicts);

            var now = DateTime.UtcNow;
            foreach (var agent in agentNames)
            {
                _locks[agent] = new AgentLock
                {
                    AgentName = agent,
                    SessionId = sessionId,
                    WatchItemTag = watchItemTag,
                    UserId = userId,
                    Source = source,
                    LockedAtUtc = now,
                };
            }

            Interlocked.Increment(ref _version);
            PersistToDisk();
            return (true, Array.Empty<AgentLock>());
        }
    }

    /// <summary>
    /// Releases all agents locked by a specific session.
    /// Called when a session completes, fails, or is cancelled.
    /// Uses _atomicLock to prevent race with TryLockAgents.
    /// </summary>
    public int ReleaseSession(string sessionId)
    {
        lock (_atomicLock)
        {
            int released = 0;
            foreach (var kvp in _locks)
            {
                if (string.Equals(kvp.Value.SessionId, sessionId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Verify value still belongs to this session before removing
                    if (_locks.TryGetValue(kvp.Key, out var current) &&
                        string.Equals(current.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        _locks.TryRemove(kvp.Key, out _);
                        released++;
                    }
                }
            }
            if (released > 0)
            {
                Interlocked.Increment(ref _version);
                PersistToDisk();
            }
            return released;
        }
    }

    /// <summary>Force-releases a single agent. WPF admin only.</summary>
    public bool ForceRelease(string agentName)
    {
        if (_locks.TryRemove(agentName, out _))
        {
            Interlocked.Increment(ref _version);
            PersistToDisk();
            return true;
        }
        return false;
    }

    /// <summary>Force-releases all agents. WPF admin emergency reset.</summary>
    public int ForceReleaseAll()
    {
        int count = _locks.Count;
        _locks.Clear();
        if (count > 0)
        {
            Interlocked.Increment(ref _version);
            PersistToDisk();
        }
        return count;
    }

    /// <summary>Returns all current locks (for dashboard/API).</summary>
    public IReadOnlyList<AgentLock> GetAllLocks()
        => _locks.Values.ToList();

    /// <summary>Returns the lock for a specific agent, or null.</summary>
    public AgentLock? GetLock(string agentName)
        => _locks.TryGetValue(agentName, out var l) ? l : null;

    /// <summary>Returns all agents locked by a specific session.</summary>
    public IReadOnlyList<string> GetSessionAgents(string sessionId)
        => _locks.Where(kvp =>
            kvp.Value.SessionId == sessionId)
            .Select(kvp => kvp.Key).ToList();

    /// <summary>
    /// Checks if all required agents are free.
    /// Returns the list of conflicts (empty = can trigger).
    /// </summary>
    public IReadOnlyList<AgentLock> CheckAvailability(
        IReadOnlyList<string> requiredAgents)
    {
        var conflicts = new List<AgentLock>();
        foreach (var agent in requiredAgents)
        {
            if (_locks.TryGetValue(agent, out var existing))
                conflicts.Add(existing);
        }
        return conflicts;
    }

    /// <summary>
    /// Detects orphaned locks � locks whose session is no longer active.
    /// </summary>
    public IReadOnlyList<AgentLock> FindOrphanedLocks(
        Func<string, bool> isSessionActive)
    {
        return _locks.Values
            .Where(l => !isSessionActive(l.SessionId))
            .ToList();
    }

    /// <summary>
    /// Validates that a session's locks are still intact.
    /// Returns agents that SHOULD be locked but aren't.
    /// Called periodically to detect lock corruption (e.g., admin force-release mid-session).
    /// </summary>
    public IReadOnlyList<string> ValidateSessionLocks(
        string sessionId,
        IReadOnlyList<string> expectedAgents)
    {
        var missing = new List<string>();
        foreach (var agent in expectedAgents)
        {
            var current = GetLock(agent);
            if (current == null || current.SessionId != sessionId)
                missing.Add(agent);
        }
        return missing;
    }

    /// <summary>The file path used for lock persistence, if configured.</summary>
    public string? PersistPath => _persistPath;

    // ── Persistence ───────────────────────────────────────────────────

    /// <summary>Current schema version for lock persistence format.</summary>
    private const int PersistSchemaVersion = 1;

    private void PersistToDisk()
    {
        if (string.IsNullOrEmpty(_persistPath)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_persistLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_persistPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var envelope = new PersistedLockEnvelope
                    {
                        SchemaVersion = PersistSchemaVersion,
                        Locks = _locks.Values.Select(l => new PersistedLock
                        {
                            AgentName = l.AgentName,
                            SessionId = l.SessionId,
                            WatchItemTag = l.WatchItemTag,
                            UserId = l.UserId,
                            Source = l.Source,
                            LockedAtUtc = l.LockedAtUtc.ToString("o"),
                        }).ToArray(),
                    };

                    var json = JsonSerializer.Serialize(envelope,
                        new JsonSerializerOptions { WriteIndented = true });

                    var tempPath = _persistPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _persistPath, overwrite: true);
                }
                catch
                {
                    // Persistence failure is non-fatal
                }
            }
        });
    }

    private void RestoreFromDisk()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);

            // Try new envelope format first
            PersistedLock[]? entries = null;
            try
            {
                var envelope = JsonSerializer.Deserialize<PersistedLockEnvelope>(json);
                if (envelope?.SchemaVersion >= 1)
                    entries = envelope.Locks;
            }
            catch (JsonException)
            {
                // Fall through to legacy format
            }

            // Fallback: legacy format (bare array of PersistedLock)
            entries ??= JsonSerializer.Deserialize<PersistedLock[]>(json);
            if (entries == null) return;

            foreach (var entry in entries)
            {
                var lockedAt = DateTime.TryParse(entry.LockedAtUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt : DateTime.UtcNow;

                _locks[entry.AgentName] = new AgentLock
                {
                    AgentName = entry.AgentName,
                    SessionId = entry.SessionId,
                    WatchItemTag = entry.WatchItemTag,
                    UserId = entry.UserId,
                    Source = entry.Source,
                    LockedAtUtc = lockedAt,
                };
            }
        }
        catch
        {
            _locks.Clear();
        }
    }

    private record PersistedLock
    {
        public string AgentName { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string WatchItemTag { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Source { get; init; } = "";
        public string LockedAtUtc { get; init; } = "";
    }

    private record PersistedLockEnvelope
    {
        public int SchemaVersion { get; init; }
        public PersistedLock[] Locks { get; init; } = [];
    }
}
