using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.SignalR;
using TestController.Api.Hubs;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

/// <summary>Request body for triggering with parameters.</summary>
public record TriggerRequest
{
    public string? BuildNumber { get; init; }
    public string? DropLocation { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public string? UserId { get; init; }
    public long? LockVersion { get; init; }
}

[ApiController]
[Route("api/execution")]
public class ExecutionController : ControllerBase
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly AgentLockManager _lockManager;
    private readonly IRealtimeNotifier _notifier;

    /// <summary>Per-tag locks to prevent TOCTOU race without serializing unrelated triggers.</summary>
    private static readonly ConcurrentDictionary<string, object> _triggerLocks = new(StringComparer.OrdinalIgnoreCase);

    public ExecutionController(
        ExecutionSessionManager sessionManager,
        IActionPipelineExecutor executor,
        IVocabularyMonitor vocabMonitor,
        IHubContext<ControllerHub> hub,
        AgentLockManager lockManager,
        IRealtimeNotifier notifier)
    {
        _sessionManager = sessionManager;
        _executor = executor;
        _vocabMonitor = vocabMonitor;
        _hub = hub;
        _lockManager = lockManager;
        _notifier = notifier;
    }

    /// <summary>GET /api/execution/sessions � list active sessions.</summary>
    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        var active = _sessionManager.GetActiveSessions();
        return Ok(new
        {
            activeCount = _sessionManager.ActiveExecutionCount,
            hasActive = _sessionManager.HasAnyActiveExecution,
            sessions = active.Select(s => new
            {
                sessionId = s.SessionId,
                watchItemTag = s.WatchItemTag,
                eventType = s.EventType,
                state = s.State.ToString(),
                startedUtc = s.StartedUtc,
                totalActions = s.SnapshotNodes.Count,
                completedActions = s.ActionResults.Count,
                passedActions = s.SucceededCount,
                failedActions = s.FailedCount,
                progressPercent = s.SnapshotNodes.Count > 0
                    ? (double)s.ActionResults.Count / s.SnapshotNodes.Count * 100
                    : 0,
            }),
        });
    }

    /// <summary>GET /api/execution/dashboard-sessions – active + recent sessions with per-agent details.</summary>
    [HttpGet("dashboard-sessions")]
    public IActionResult GetDashboardSessions()
    {
        var active = _sessionManager.GetActiveSessions()
            .Select(MapDashboardSession).ToList();
        var history = _sessionManager.GetHistory(20)
            .Select(MapDashboardSession).ToList();

        return Ok(new { active, history });
    }

    /// <summary>GET /api/execution/status – current execution state overview.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            isExecuting = _sessionManager.HasAnyActiveExecution,
            activeCount = _sessionManager.ActiveExecutionCount,
        });
    }

    /// <summary>
    /// GET /api/execution/demo-sessions – returns fake sessions for dashboard UI testing.
    /// Use this when no real pipeline is running to verify the dashboard renders correctly.
    /// </summary>
    [HttpGet("demo-sessions")]
    public IActionResult GetDemoSessions()
    {
        var now = DateTime.UtcNow;

        var active = new[]
        {
            new
            {
                sessionId = "demo01",
                watchItemTag = "Deploy.WebApi",
                userId = "developer1",
                source = "WebClient",
                status = "Running",
                startedUtc = now.AddMinutes(-2).ToString("o"),
                elapsed = "02:15",
                lockedAgents = new[] { "Agent-01", "Agent-02", "Agent-03" },
                buildNumber = "2026.05.04.1",
                totalActions = 8,
                completedActions = 5,
                passedActions = 5,
                failedActions = 0,
                progressPercent = 62,
                agents = new object[]
                {
                    new
                    {
                        agentName = "Agent-01", status = "Success", completedCount = 3, totalCount = 3, progressPercent = 100,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-01", command = @"\\server\install.cmd", status = "Success", exitCode = 0, duration = "45s" },
                            new { tag = "Run Smoke Tests", actionType = "RunRemoteCommand", agentName = "Agent-01", command = @"\\server\smoke.cmd", status = "Success", exitCode = 0, duration = "30s" },
                            new { tag = "Reboot", actionType = "RunRemoteCommand", agentName = "Agent-01", command = "shutdown /r /t 0", status = "Success", exitCode = 0, duration = "60s" },
                        }
                    },
                    new
                    {
                        agentName = "Agent-02", status = "Executing", completedCount = 1, totalCount = 3, progressPercent = 33,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-02", command = @"\\server\install.cmd", status = "Success", exitCode = 0, duration = "48s" },
                            new { tag = "Run Integration", actionType = "RunRemoteCommand", agentName = "Agent-02", command = @"\\server\integration.cmd", status = "Running", progressPercent = 65 },
                            new { tag = "Email: Results", actionType = "SendMail", agentName = "Agent-02", command = "qa-team@company.com", status = "Pending" },
                        }
                    },
                    new
                    {
                        agentName = "Agent-03", status = "Executing", completedCount = 1, totalCount = 2, progressPercent = 50,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-03", command = @"\\server\install.cmd", status = "Success", exitCode = 0, duration = "52s" },
                            new { tag = "Run Perf Suite", actionType = "RunRemoteCommand", agentName = "Agent-03", command = @"\\server\perf.cmd", status = "Running", progressPercent = 30 },
                        }
                    },
                }
            }
        };

        var history = new[]
        {
            new
            {
                sessionId = "demo02",
                watchItemTag = "Nightly.FullSuite",
                userId = "scheduler",
                source = "WebClient",
                status = "Failed",
                startedUtc = now.AddMinutes(-16).ToString("o"),
                elapsed = "15:42",
                lockedAgents = new[] { "Agent-04", "Agent-05" },
                buildNumber = "2026.05.03.7",
                totalActions = 6,
                completedActions = 5,
                passedActions = 4,
                failedActions = 1,
                progressPercent = 100,
                agents = new object[]
                {
                    new
                    {
                        agentName = "Agent-04", status = "Success", completedCount = 3, totalCount = 3, progressPercent = 100,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-04", command = @"\\nightly\install.cmd", status = "Success", exitCode = 0, duration = "01:10" },
                            new { tag = "Run Unit Tests", actionType = "RunRemoteCommand", agentName = "Agent-04", command = "dotnet test", status = "Success", exitCode = 0, duration = "08:30" },
                            new { tag = "Collect Results", actionType = "RunCommand", agentName = "Agent-04", command = @"copy *.trx \\results", status = "Success", exitCode = 0, duration = "5s" },
                        }
                    },
                    new
                    {
                        agentName = "Agent-05", status = "Failed", completedCount = 2, totalCount = 3, progressPercent = 67,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-05", command = @"\\nightly\install.cmd", status = "Success", exitCode = 0, duration = "01:15" },
                            new { tag = "Run E2E Tests", actionType = "RunRemoteCommand", agentName = "Agent-05", command = @"\\nightly\e2e.cmd", status = "Failed", exitCode = 1, errorMessage = "3 test cases failed: LoginTest, PaymentTest, CheckoutTest", duration = "12:05" },
                            new { tag = "Cleanup", actionType = "RunRemoteCommand", agentName = "Agent-05", command = @"\\nightly\cleanup.cmd", status = "Skipped" },
                        }
                    },
                }
            },
            new
            {
                sessionId = "demo03",
                watchItemTag = "Build.QuickVerify",
                userId = "ci-bot",
                source = "WebClient",
                status = "Success",
                startedUtc = now.AddMinutes(-4).ToString("o"),
                elapsed = "03:20",
                lockedAgents = new[] { "Agent-01" },
                buildNumber = "2026.05.04.3",
                totalActions = 2,
                completedActions = 2,
                passedActions = 2,
                failedActions = 0,
                progressPercent = 100,
                agents = new object[]
                {
                    new
                    {
                        agentName = "Agent-01", status = "Success", completedCount = 2, totalCount = 2, progressPercent = 100,
                        actions = new object[]
                        {
                            new { tag = "Install Build", actionType = "RunRemoteCommand", agentName = "Agent-01", command = @"\\server\install.cmd", status = "Success", exitCode = 0, duration = "40s" },
                            new { tag = "Quick BVT", actionType = "RunRemoteCommand", agentName = "Agent-01", command = @"\\server\bvt.cmd", status = "Success", exitCode = 0, duration = "02:30" },
                        }
                    },
                }
            },
        };

        return Ok(new { active, history });
    }

    /// <summary>GET /api/execution/{sessionId} � detailed session info.</summary>
    [HttpGet("{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session == null) return NotFound();
        return Ok(ToSessionDto(session));
    }

    /// <summary>
    /// POST /api/execution/trigger/{watchItemTag}?eventType=Renamed � trigger a WatchItem.
    /// Accepts an optional JSON body with build number, drop location, and custom parameters.
    /// Uses agent-level locking to prevent concurrent pipelines from sharing agents.
    /// </summary>
    [HttpPost("trigger/{watchItemTag}")]
    public async Task<IActionResult> TriggerWatchItem(
        string watchItemTag,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TriggerRequest? request = null,
        [FromQuery] string? eventType = null)
    {
        var userId = request?.UserId
            ?? HttpContext.Request.Headers["X-User-Id"].FirstOrDefault()
            ?? "anonymous";
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "WebClient";

        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, watchItemTag, StringComparison.OrdinalIgnoreCase));

        if (watchItem == null)
            return NotFound(new { error = $"WatchItem '{watchItemTag}' not found" });

        if (watchItem.Events.Count == 0)
            return BadRequest(new { error = $"WatchItem '{watchItemTag}' has no events" });

        var evt = eventType is not null
            ? watchItem.Events.FirstOrDefault(e =>
                string.Equals(e.Type, eventType, StringComparison.OrdinalIgnoreCase))
            : watchItem.Events[0];

        if (evt == null)
            return BadRequest(new { error = $"Event type '{eventType}' not found on WatchItem '{watchItemTag}'" });

        // Build parameters (needed for agent variable resolution)
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paramFile = WatchListHelpers.FindInitializeFile(watchItem);
        if (paramFile != null && System.IO.File.Exists(paramFile))
        {
            var entries = ParameterResolver.ParseParameterFile(paramFile);
            foreach (var (key, value) in entries)
            {
                parameters[key] = value;
                if (key.StartsWith('_'))
                    parameters[key[1..]] = value;
            }
        }

        if (!string.IsNullOrEmpty(request?.BuildNumber))
        {
            parameters["_BuildNumber"] = request.BuildNumber;
            parameters["BuildNumber"] = request.BuildNumber;
        }
        if (!string.IsNullOrEmpty(request?.DropLocation))
        {
            parameters["_DropLocation"] = request.DropLocation;
            parameters["DropLocation"] = request.DropLocation;
        }
        if (request?.Parameters != null)
        {
            foreach (var kvp in request.Parameters)
            {
                parameters[kvp.Key] = kvp.Value;
                if (kvp.Key.StartsWith('_'))
                    parameters[kvp.Key[1..]] = kvp.Value;
            }
        }

        // Extract required agents (resolved from variables)
        var requiredAgents = AgentResolver.ExtractAgentNames(watchItem, parameters);

        // Optimistic locking: reject if lock state changed since pre-flight check
        if (request?.LockVersion.HasValue == true &&
            request.LockVersion.Value != _lockManager.Version)
        {
            return Conflict(new
            {
                error = "Lock state changed",
                message = "The agent availability changed since you last checked. Please refresh and try again.",
                isStaleState = true,
                yourVersion = request.LockVersion.Value,
                currentVersion = _lockManager.Version,
            });
        }

        // Atomic check-and-lock inside a per-tag lock to prevent TOCTOU race.
        var tagLock = _triggerLocks.GetOrAdd(watchItemTag, _ => new object());
        string sessionId;
        lock (tagLock)
        {
            if (_sessionManager.HasActiveExecution(watchItemTag))
                return Conflict(new { error = $"WatchItem '{watchItemTag}' is already running" });

            // Try to lock all agents atomically
            sessionId = Guid.NewGuid().ToString("N")[..12];
            if (requiredAgents.Count > 0)
            {
                var (locked, conflicts) = _lockManager.TryLockAgents(
                    requiredAgents, sessionId, watchItemTag, userId, source);

                if (!locked)
                {
                    // Detect "just missed it" race: conflict lock was acquired < 5s ago
                    var newestConflict = conflicts.OrderByDescending(c => c.LockedAtUtc).First();
                    var lockAge = DateTime.UtcNow - newestConflict.LockedAtUtc;
                    var isRace = lockAge.TotalSeconds < 5;

                    return Conflict(new
                    {
                        error = "Agents are busy",
                        isRaceCondition = isRace,
                        message = isRace
                            ? $"Another user just triggered '{newestConflict.WatchItemTag}' moments ago. " +
                              $"The agent was free when you opened the dialog but was claimed by {newestConflict.UserId} first."
                            : $"Cannot start '{watchItemTag}' � {conflicts.Count} required agent(s) are locked by other sessions",
                        conflicts = conflicts.Select(c => new
                        {
                            c.AgentName,
                            lockedBy = c.UserId,
                            pipeline = c.WatchItemTag,
                            sessionId = c.SessionId,
                            source = c.Source,
                            lockedSince = c.LockedAtUtc,
                            duration = FormatDuration(DateTime.UtcNow - c.LockedAtUtc),
                        }),
                        requiredAgents,
                        yourUserId = userId,
                        retryAdvice = isRace
                            ? "Try again in a few seconds � or wait for the other pipeline to finish."
                            : "Wait for the blocking pipeline to complete.",
                    });
                }
            }

            var session = _sessionManager.BeginSession(
                watchItemTag, evt.Type,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                evt.Children.ToList(),
                sessionId);
            session.UserId = userId;
            session.Source = source;
            session.LockedAgents = requiredAgents.ToArray();
        }

        if (paramFile != null && parameters.Count > 0)
        {
            try
            {
                var lines = parameters
                    .Where(kvp => kvp.Key.StartsWith('_'))
                    .Select(kvp => $"{kvp.Key},{kvp.Value}");
                await System.IO.File.WriteAllLinesAsync(paramFile, lines);
            }
            catch { /* best effort */ }
        }

        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = watchItem.Path,
            SessionId = sessionId,
            StartedUtc = DateTime.UtcNow,
            Parameters = parameters,
        };

        // Broadcast lock state to all clients
        BroadcastLockChange("Pipeline started");

        // Fire and forget � ExecuteEventTrackedAsync handles session lifecycle
        _ = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteEventTrackedAsync(watchItemTag, evt, ctx, CancellationToken.None);

                var completedSession = _sessionManager.GetSession(sessionId)
                    ?? _sessionManager.GetLastSession(watchItemTag);
                var finalState = completedSession?.State switch
                {
                    SessionState.Completed => "Success",
                    SessionState.PartialFailure => "PartialFailure",
                    SessionState.Failed => "Failed",
                    _ => "Success",
                };

                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = finalState,
                    passed = completedSession?.SucceededCount ?? 0,
                    failed = completedSession?.FailedCount ?? 0,
                    total = completedSession?.TotalActions ?? 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
            catch (OperationCanceledException)
            {
                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = "Cancelled",
                    passed = 0, failed = 0, total = 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
            catch (Exception ex)
            {
                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = "Failed",
                    error = ex.Message,
                    passed = 0, failed = 0, total = 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
            finally
            {
                // Release locks only if not already released (e.g., by cancel endpoint)
                var released = _lockManager.ReleaseSession(sessionId);
                if (released > 0)
                    BroadcastLockChange("Pipeline completed");
            }
        });

        await _hub.Clients.Group("global").SendAsync("ExecutionStarted", new
        {
            sessionId,
            watchItemTag,
            eventType = evt.Type,
            startTime = DateTime.UtcNow.ToString("o"),
            source,
            userId,
            lockedAgents = requiredAgents,
        });

        return Accepted(new
        {
            sessionId,
            message = $"WatchItem '{watchItemTag}' triggered (event: {evt.Type})",
        });
    }

    /// <summary>GET /api/execution/available-builds?basePath=...</summary>
    [HttpGet("available-builds")]
    public IActionResult GetAvailableBuilds([FromQuery] string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return Ok(new { builds = Array.Empty<object>() });

        if (!System.IO.Directory.Exists(basePath))
            return NotFound(new { error = $"Path not found: {basePath}" });

        var builds = System.IO.Directory.GetDirectories(basePath)
            .Select(d => new
            {
                name = System.IO.Path.GetFileName(d),
                path = d,
                modified = System.IO.Directory.GetLastWriteTime(d),
            })
            .OrderByDescending(b => b.modified)
            .Take(50)
            .ToList();

        return Ok(new { builds });
    }

    // ?? Lock & availability endpoints ????????????????????????????????

    /// <summary>GET /api/execution/locks � all current agent locks.</summary>
    [HttpGet("locks")]
    public IActionResult GetLocks()
    {
        var locks = _lockManager.GetAllLocks()
            .Select(l => new
            {
                l.AgentName,
                l.SessionId,
                l.WatchItemTag,
                l.UserId,
                l.Source,
                l.LockedAtUtc,
                duration = FormatDuration(DateTime.UtcNow - l.LockedAtUtc),
            });

        return Ok(new { locks, lockVersion = _lockManager.Version, timestamp = DateTime.UtcNow });
    }

    /// <summary>GET /api/execution/can-trigger/{watchItemTag} � pre-flight availability check.</summary>
    [HttpGet("can-trigger/{watchItemTag}")]
    public IActionResult CanTrigger(string watchItemTag)
    {
        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, watchItemTag, StringComparison.OrdinalIgnoreCase));
        if (watchItem == null)
            return NotFound(new { error = "WatchItem not found" });

        var parameters = LoadParametersForWatchItem(watchItem);
        var requiredAgents = AgentResolver.ExtractAgentNames(watchItem, parameters);
        var conflicts = _lockManager.CheckAvailability(requiredAgents);

        return Ok(new
        {
            canTrigger = conflicts.Count == 0,
            lockVersion = _lockManager.Version,
            watchItemTag,
            requiredAgents,
            conflicts = conflicts.Select(c => new
            {
                c.AgentName,
                lockedBy = c.UserId,
                pipeline = c.WatchItemTag,
                sessionId = c.SessionId,
                source = c.Source,
                since = c.LockedAtUtc,
                duration = FormatDuration(DateTime.UtcNow - c.LockedAtUtc),
            }),
        });
    }

    // ?? Reconnection & session endpoints ?????????????????????????????

    /// <summary>GET /api/execution/reconnect � returns user's active sessions for reconnection.</summary>
    [HttpGet("reconnect")]
    public IActionResult Reconnect()
    {
        var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(userId))
            return Ok(new { activeSessions = Array.Empty<object>() });

        var active = _sessionManager.GetActiveSessions()
            .Where(s => string.Equals(s.UserId, userId, StringComparison.OrdinalIgnoreCase))
            .Select(s => new
            {
                s.SessionId,
                s.WatchItemTag,
                s.Source,
                status = s.State.ToString(),
                elapsed = (DateTime.UtcNow - s.StartedUtc).ToString(@"hh\:mm\:ss"),
                s.LockedAgents,
                logCount = s.GetRecentLogs(0).Count,
            })
            .ToList();

        return Ok(new { userId, activeSessions = active });
    }

    /// <summary>GET /api/execution/{sessionId}/recent-logs � backfill logs after reconnect.</summary>
    [HttpGet("{sessionId}/recent-logs")]
    public IActionResult GetRecentLogs(string sessionId, [FromQuery] int count = 200)
    {
        var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "";
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "WebClient";

        var session = _sessionManager.GetSession(sessionId);
        if (session == null)
            return NotFound(new { error = "Session not found" });

        if (source != "WPF" &&
            !string.IsNullOrEmpty(userId) &&
            !string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(403, new { error = "Not your session", sessionOwner = session.UserId });
        }

        var logs = session.GetRecentLogs(count);
        return Ok(new
        {
            sessionId,
            logs = logs.Select(l => new
            {
                timestamp = l.Timestamp.ToString("HH:mm:ss.fff"),
                l.Category,
                l.Message,
            }),
            count = logs.Count,
            sessionStatus = session.State.ToString(),
            elapsed = (DateTime.UtcNow - session.StartedUtc).ToString(@"hh\:mm\:ss"),
        });
    }

    /// <summary>GET /api/execution/my-sessions � user's own sessions.</summary>
    [HttpGet("my-sessions")]
    public IActionResult GetMySessions()
    {
        var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "";

        var active = _sessionManager.GetActiveSessions()
            .Where(s => string.Equals(s.UserId, userId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var history = _sessionManager.GetHistory(50)
            .Where(s => string.Equals(s.UserId, userId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Ok(new
        {
            userId,
            active = active.Select(ToSessionDto),
            history = history.Select(ToSessionDto),
        });
    }

    // ?? Cancel endpoints ????????????????????????????????????????????

    /// <summary>POST /api/execution/cancel � cancel all running sessions.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelAll()
    {
        // Snapshot active sessions (ID + tag) before cancellation moves them to history
        var activeSessions = _sessionManager.GetActiveSessions()
            .Select(s => new { s.SessionId, s.WatchItemTag }).ToList();

        var cancelledTags = _sessionManager.CancelAll();

        // Release locks for the actual cancelled sessions
        foreach (var s in activeSessions)
            _lockManager.ReleaseSession(s.SessionId);

        // Notify per-session with sessionId so WebClient can correlate
        foreach (var s in activeSessions.Where(a => cancelledTags.Contains(a.WatchItemTag)))
        {
            await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
            {
                sessionId = s.SessionId,
                watchItemTag = s.WatchItemTag,
                state = "Cancelled",
                timestamp = DateTime.UtcNow.ToString("o"),
            });
        }

        if (cancelledTags.Count > 0)
            BroadcastLockChange("All sessions cancelled");

        return Ok(new
        {
            message = $"Cancelled {cancelledTags.Count} execution(s).",
            activeExecutions = _sessionManager.ActiveExecutionCount,
        });
    }

    /// <summary>POST /api/execution/{sessionId}/cancel � cancel a specific session (ownership enforced).</summary>
    [HttpPost("{sessionId}/cancel")]
    public async Task<IActionResult> CancelSession(string sessionId)
    {
        var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "";
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "WebClient";

        var session = _sessionManager.GetSession(sessionId);
        if (session == null || session.State != SessionState.Running)
            return NotFound(new { error = "Session not found or not running" });

        // Ownership check: WebClient users can only cancel their own sessions
        if (source != "WPF" &&
            !string.IsNullOrEmpty(userId) &&
            !string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(403, new
            {
                error = "Cannot cancel another user's session",
                sessionOwner = session.UserId,
                yourId = userId,
            });
        }

        var cancelled = _sessionManager.CancelSession(sessionId);
        if (!cancelled) return NotFound(new { error = "Session not found or not running" });

        _lockManager.ReleaseSession(sessionId);
        BroadcastLockChange($"Session {sessionId} cancelled");

        await _hub.Clients.Group("global").SendAsync("ExecutionCancelled", new
        {
            sessionId,
            timestamp = DateTime.UtcNow.ToString("o"),
        });

        return Ok(new { message = "Cancellation requested", cancelledBy = userId });
    }

    // ?? Force-release endpoints (admin only) ?????????????????????????

    /// <summary>POST /api/execution/force-release/{agentName} � admin force-release a single agent.</summary>
    [HttpPost("force-release/{agentName}")]
    public IActionResult ForceReleaseAgent(string agentName)
    {
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "Unknown";
        if (source is not ("WPF" or "WebClient"))
            return StatusCode(403, new { error = "Only admin clients can force-release agents" });

        var currentLock = _lockManager.GetLock(agentName);
        if (currentLock == null)
            return NotFound(new { error = $"Agent '{agentName}' is not locked" });

        _lockManager.ForceRelease(agentName);
        BroadcastLockChange($"Force-released: {agentName}");

        return Ok(new
        {
            message = $"Agent '{agentName}' force-released",
            previousLock = new { currentLock.SessionId, currentLock.UserId, currentLock.WatchItemTag },
        });
    }

    /// <summary>POST /api/execution/force-release-all � admin emergency release all locks.</summary>
    [HttpPost("force-release-all")]
    public IActionResult ForceReleaseAll()
    {
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "Unknown";
        if (source is not ("WPF" or "WebClient"))
            return StatusCode(403, new { error = "Admin only" });

        var count = _lockManager.ForceReleaseAll();
        BroadcastLockChange("All locks force-released");

        return Ok(new { message = $"Released {count} agent locks" });
    }

    // ?? Helpers ??????????????????????????????????????????????????????

    private void BroadcastLockChange(string reason)
    {
        var locks = _lockManager.GetAllLocks()
            .Select(l => new AgentLockInfo
            {
                AgentName = l.AgentName,
                SessionId = l.SessionId,
                WatchItemTag = l.WatchItemTag,
                UserId = l.UserId,
                Source = l.Source,
                LockedAtUtc = l.LockedAtUtc,
                Duration = FormatDuration(DateTime.UtcNow - l.LockedAtUtc),
            }).ToList();

        _ = _notifier.NotifyAgentLocksChanged(new AgentLocksChangedEvent
        {
            Locks = locks,
            Reason = reason,
        });
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }

    /// <summary>Maps SessionState enum to WebClient-expected status strings.</summary>
    private static string MapSessionStatus(SessionState state) => state switch
    {
        SessionState.Running         => "Running",
        SessionState.Completed       => "Success",
        SessionState.PartialFailure  => "Failed",
        SessionState.Failed          => "Failed",
        _                            => "Running",
    };

    /// <summary>Maps ActionOutcome enum to WebClient-expected status strings.</summary>
    private static string MapActionStatus(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success    => "Success",
        ActionOutcome.Failed     => "Failed",
        ActionOutcome.Terminated => "Failed",
        ActionOutcome.TimedOut   => "Failed",
        ActionOutcome.Unknown    => "Pending",
        _                        => "Pending",
    };

    private static Dictionary<string, string> LoadParametersForWatchItem(WatchItemConfig watchItem)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paramFile = WatchListHelpers.FindInitializeFile(watchItem);
        if (paramFile != null && System.IO.File.Exists(paramFile))
        {
            foreach (var (key, value) in ParameterResolver.ParseParameterFile(paramFile))
            {
                parameters[key] = value;
                if (key.StartsWith('_'))
                    parameters[key[1..]] = value;
            }
        }
        return parameters;
    }

    private static object ToSessionDto(ExecutionSession s) => new
    {
        sessionId = s.SessionId,
        watchItemTag = s.WatchItemTag,
        eventType = s.EventType,
        startedUtc = s.StartedUtc,
        completedUtc = s.CompletedUtc,
        state = s.State.ToString(),
        userId = s.UserId,
        source = s.Source,
        lockedAgents = s.LockedAgents,
        totalActions = s.TotalActions,
        succeededCount = s.SucceededCount,
        failedCount = s.FailedCount,
        summary = s.SummaryText,
    };

    private static object MapDashboardSession(ExecutionSession s) => new
    {
        sessionId = s.SessionId,
        watchItemTag = s.WatchItemTag,
        userId = s.UserId,
        source = s.Source,
        status = MapSessionStatus(s.State),
        startedUtc = s.StartedUtc.ToString("o"),
        elapsed = (DateTime.UtcNow - s.StartedUtc).ToString(@"hh\:mm\:ss"),
        lockedAgents = s.LockedAgents,
        buildNumber = s.ResolvedParameters
            .GetValueOrDefault("_BuildNumber", ""),
        totalActions = s.SnapshotNodes.Count,
        completedActions = s.ActionResults.Count,
        passedActions = s.SucceededCount,
        failedActions = s.FailedCount,
        progressPercent = s.SnapshotNodes.Count > 0
            ? (int)((double)s.ActionResults.Count / s.SnapshotNodes.Count * 100)
            : 0,
        agents = s.GetAgentSummaries().Select(a => new
        {
            agentName = a.AgentName,
            status = a.Status,
            completedCount = a.CompletedCount,
            totalCount = a.TotalCount,
            progressPercent = a.TotalCount > 0
                ? (int)((double)a.CompletedCount / a.TotalCount * 100)
                : 0,
            actions = a.Actions.Select(act => new
            {
                tag = act.ActionTag,
                actionType = act.ActionType,
                agentName = act.AgentName,
                command = act.Command,
                status = MapActionStatus(act.Outcome),
                exitCode = act.ExitCode,
                errorMessage = act.ErrorMessage,
                duration = act.Duration.ToString(@"mm\:ss"),
            }),
        }),
    };
}
