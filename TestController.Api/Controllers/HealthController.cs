using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly CachedBuildResultsProvider _buildResults;
    private readonly AgentLockManager _lockManager;
    private readonly IAppLogger _appLogger;

    public HealthController(
        IVocabularyMonitor vocabMonitor,
        ExecutionSessionManager sessionManager,
        IAgentGrpcDispatcher dispatcher,
        CachedBuildResultsProvider buildResults,
        AgentLockManager lockManager,
        IAppLogger appLogger)
    {
        _vocabMonitor = vocabMonitor;
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _buildResults = buildResults;
        _lockManager = lockManager;
        _appLogger = appLogger;
    }

    /// <summary>GET /api/health � lightweight liveness check.</summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        var config = _vocabMonitor.CurrentConfig;
        return Ok(new
        {
            status = "ok",
            timestamp = DateTime.UtcNow,
            server = Environment.MachineName,
            components = new
            {
                watchList = new { loaded = config != null, watchItems = config?.WatchItems.Count ?? 0 },
                agents = new { registered = _dispatcher.RegisteredAgentCount },
                execution = new { activeSessions = _sessionManager.ActiveExecutionCount },
                signalR = new { status = "available" },
            },
        });
    }

    /// <summary>GET /api/health/diagnostics � detailed system info for debugging.</summary>
    [HttpGet("health/diagnostics")]
    public IActionResult Diagnostics()
    {
        var config = _vocabMonitor.CurrentConfig;
        var process = Process.GetCurrentProcess();

        return Ok(new
        {
            watchList = new
            {
                filePath = config?.FilePath ?? "(not loaded)",
                watchItemCount = config?.WatchItems.Count ?? 0,
                templateCount = config?.Templates.Count ?? 0,
                watchItems = config?.WatchItems.Select(wi => new
                {
                    tag = wi.Tag,
                    isEnabled = wi.IsEnabled,
                    eventCount = wi.Events.Count,
                }) ?? [],
            },
            agents = _dispatcher.RegisteredAgents.Select(name => new
            {
                name,
                address = _dispatcher.GetAgentAddress(name),
                health = _dispatcher.GetAgentHealth(name) is { } h ? new
                {
                    isHealthy = h.IsHealthy,
                    consecutiveFailures = h.ConsecutiveFailures,
                    lastSuccessUtc = h.LastSuccessUtc,
                } : null,
            }),
            execution = new
            {
                activeSessions = _sessionManager.GetActiveSessions().Select(s => new
                {
                    sessionId = s.SessionId,
                    watchItemTag = s.WatchItemTag,
                    eventType = s.EventType,
                    state = s.State.ToString(),
                    startedUtc = s.StartedUtc,
                    actions = s.TotalActions,
                    passed = s.SucceededCount,
                    failed = s.FailedCount,
                }),
                recentHistory = _sessionManager.GetHistory(5).Select(s => new
                {
                    sessionId = s.SessionId,
                    watchItemTag = s.WatchItemTag,
                    state = s.State.ToString(),
                    completedUtc = s.CompletedUtc,
                    summary = s.SummaryText,
                }),
            },
            environment = new
            {
                machineName = Environment.MachineName,
                dotnetVersion = Environment.Version.ToString(),
                processId = Environment.ProcessId,
                workingDirectory = Environment.CurrentDirectory,
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"dd\.hh\:mm\:ss"),
            },
            resultsCache = _buildResults.GetStats(),
            agentLocks = new
            {
                currentLocks = _lockManager.GetAllLocks().Count,
                lockVersion = _lockManager.Version,
                persistFilePath = _lockManager.PersistPath ?? "(in-memory only)",
                persistFileExists = _lockManager.PersistPath != null && System.IO.File.Exists(_lockManager.PersistPath),
                orphanedLocks = _lockManager.FindOrphanedLocks(
                    sid => _sessionManager.GetSession(sid) != null).Count,
                locks = _lockManager.GetAllLocks().Select(l => new
                {
                    l.AgentName, l.SessionId, l.WatchItemTag, l.UserId, l.Source, l.LockedAtUtc,
                }),
            },
        });
    }

    /// <summary>
    /// GET /api/health/logs � recent structured log entries for debugging.
    /// Supports filtering by component, level, and correlation ID.
    /// </summary>
    [HttpGet("health/logs")]
    public IActionResult GetLogs(
        [FromQuery] int count = 200,
        [FromQuery] string? component = null,
        [FromQuery] string? level = null,
        [FromQuery] string? correlationId = null)
    {
        var entries = _appLogger.GetRecentEntries(Math.Min(count, 1000));

        if (!string.IsNullOrEmpty(component))
            entries = entries.Where(e => e.Category.Contains(component, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(level))
            entries = entries.Where(e => e.Level.ToString().Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(correlationId))
            entries = entries.Where(e => e.CorrelationId == correlationId).ToList();

        return Ok(new
        {
            entries = entries.Select(e => new
            {
                seq = e.Sequence,
                time = e.Timestamp.ToString("HH:mm:ss.fff"),
                level = e.Level.ToString(),
                correlationId = e.CorrelationId,
                component = e.Category,
                message = e.Message,
                elapsedMs = e.ElapsedMs,
                exception = e.Exception,
            }),
            count = entries.Count,
        });
    }

    /// <summary>
    /// GET /api/health/log-files � lists available log files with sizes.
    /// </summary>
    [HttpGet("health/log-files")]
    public IActionResult GetLogFiles()
    {
        var logDir = (_appLogger as AppLogger)?.LogDirectory ?? AppLogger.DefaultLogDirectory;
        if (!Directory.Exists(logDir))
            return Ok(new { files = Array.Empty<object>(), path = logDir });

        var files = Directory.GetFiles(logDir, "*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new
            {
                name = f.Name,
                sizeMB = Math.Round(f.Length / 1024.0 / 1024.0, 2),
                modified = f.LastWriteTimeUtc,
            })
            .ToList();

        return Ok(new { files, path = logDir });
    }

    /// <summary>
    /// GET /api/health/log-files/{fileName}?tail=100 � returns the last N lines of a log file.
    /// </summary>
    [HttpGet("health/log-files/{fileName}")]
    public IActionResult GetLogFileContent(string fileName, [FromQuery] int tail = 100)
    {
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return BadRequest(new { error = "Invalid filename" });

        var logDir = (_appLogger as AppLogger)?.LogDirectory ?? AppLogger.DefaultLogDirectory;
        var filePath = Path.Combine(logDir, fileName);
        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = $"Log file not found: {fileName}" });

        try
        {
            // Read with FileShare.ReadWrite so we don't block the logger
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
                allLines.Add(line);

            var lines = allLines.TakeLast(Math.Min(tail, 1000)).ToList();
            return Ok(new { fileName, lines, count = lines.Count, totalLines = allLines.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to read log file: {ex.Message}" });
        }
    }

    /// <summary>
    /// GET /api/health/crashes — returns crash dump info and last crash summary.
    /// </summary>
    [HttpGet("health/crashes")]
    public IActionResult GetCrashes()
    {
        var lastCrash = CrashDumpHelper.GetLastCrashSummary();
        var dumps = CrashDumpHelper.GetCrashDumps();

        return Ok(new
        {
            lastCrash,
            dumps,
            crashLogPath = CrashDumpHelper.CrashLogPath,
            retentionDays = CrashDumpHelper.DumpRetentionDays,
        });
    }
}
