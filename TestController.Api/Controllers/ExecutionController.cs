using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.SignalR;
using TestController.Api.Contracts;
using TestController.Api.Hubs;
using TestController.Api.Interceptors;
using TestController.Api.Security;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Core.Maintenance;

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
[Authorize(Policy = SecurityPolicies.User)]
public class ExecutionController : ControllerBase
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly AgentLockManager _lockManager;
    private readonly IRealtimeNotifier _notifier;
    private readonly IAppLogger _appLogger;
    private readonly ISessionOwnershipChecker _ownershipChecker;
    private readonly ISecurityAuditLogger _auditLogger;
    private readonly IEventAggregator _events;
    private readonly ILockRegistry? _lockRegistry;
    private readonly IAuthenticationModeProvider _modeProvider;
    private readonly IMaintenanceStateStore? _maintenanceState;

    /// <summary>Per-tag locks to prevent TOCTOU race without serializing unrelated triggers.</summary>
    private static readonly ConcurrentDictionary<string, object> _triggerLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Broadcast timeout to prevent slow SignalR clients from blocking request handlers.</summary>
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(5);

    public ExecutionController(
        ExecutionSessionManager sessionManager,
        IActionPipelineExecutor executor,
        IVocabularyMonitor vocabMonitor,
        IHubContext<ControllerHub> hub,
        AgentLockManager lockManager,
        IRealtimeNotifier notifier,
        IAppLogger appLogger,
        ISessionOwnershipChecker ownershipChecker,
        ISecurityAuditLogger auditLogger,
        IEventAggregator events,
        IAuthenticationModeProvider modeProvider,
        ILockRegistry? lockRegistry = null,
        IMaintenanceStateStore? maintenanceState = null)
    {
        _sessionManager = sessionManager;
        _executor = executor;
        _vocabMonitor = vocabMonitor;
        _hub = hub;
        _lockManager = lockManager;
        _notifier = notifier;
        _appLogger = appLogger;
        _ownershipChecker = ownershipChecker;
        _auditLogger = auditLogger;
        _events = events;
        _modeProvider = modeProvider;
        _lockRegistry = lockRegistry;
        _maintenanceState = maintenanceState;
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

        // Attribution (display only — NOT an authorization input): the logical owner is the
        // triggering user; OS execution identity remains the Controller. Display name prefers
        // the authenticated principal, falling back to the supplied userId.
        var displayName = HttpContext.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = userId;
        var role = _modeProvider.ResolveRole(HttpContext.User ?? new System.Security.Claims.ClaimsPrincipal()).ToString();

        // P4-1: in Secured mode, require Pipeline_Trigger for the target pipeline.
        // No-op in Default mode (preserves current behaviour).
        if (!await IsRbacAuthorizedAsync(Permission.Pipeline_Trigger, watchItemTag, HttpContext.RequestAborted))
            return StatusCode(403, ApiErrorFactory.Forbidden($"Not authorized to trigger pipeline '{watchItemTag}'."));

        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, watchItemTag, StringComparison.OrdinalIgnoreCase));

        if (watchItem == null)
            return NotFound(ApiErrorFactory.InvalidTag(watchItemTag));

        if (watchItem.Events.Count == 0)
            return BadRequest(ApiErrorFactory.BadRequest($"WatchItem '{watchItemTag}' has no events"));

        var evt = eventType is not null
            ? watchItem.Events.FirstOrDefault(e =>
                string.Equals(e.Type, eventType, StringComparison.OrdinalIgnoreCase))
            : watchItem.Events[0];

        if (evt == null)
            return BadRequest(ApiErrorFactory.BadRequest($"Event type '{eventType}' not found on WatchItem '{watchItemTag}'"));

        // Build parameters (needed for agent variable resolution)
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paramFiles = WatchListHelpers.FindInitializeFiles(watchItem);
        foreach (var file in paramFiles)
        {
            if (!System.IO.File.Exists(file))
                continue;

            foreach (var (key, value) in ParameterResolver.ParseParameterFile(file))
            {
                // First declaration wins, so a single-file pipeline behaves exactly as before and
                // additional files only contribute keys the earlier ones did not define.
                if (!parameters.ContainsKey(key))
                    parameters[key] = value;
                if (key.StartsWith('_'))
                    parameters.TryAdd(key[1..], value);
            }
        }

        // Caller-supplied values only. File-sourced parameters above are admin-authored (editing them already
        // requires controller filesystem access) and stay untouched, so existing pipelines are unaffected.
        if (TriggerParameterValidator.Validate(request) is { } rejection)
            return BadRequest(ApiErrorFactory.BadRequest(rejection));

        // Tracked separately from `parameters` so the write-back can overlay ONLY what the caller sent
        // onto each file's own contents, instead of copying every file's parameters into every other file.
        var callerOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(request?.BuildNumber))
        {
            parameters["_BuildNumber"] = request.BuildNumber;
            parameters["BuildNumber"] = request.BuildNumber;
            callerOverrides["_BuildNumber"] = request.BuildNumber;
        }
        if (!string.IsNullOrEmpty(request?.DropLocation))
        {
            parameters["_DropLocation"] = request.DropLocation;
            parameters["DropLocation"] = request.DropLocation;
            callerOverrides["_DropLocation"] = request.DropLocation;
        }
        if (request?.Parameters != null)
        {
            foreach (var kvp in request.Parameters)
            {
                parameters[kvp.Key] = kvp.Value;
                if (kvp.Key.StartsWith('_'))
                {
                    parameters[kvp.Key[1..]] = kvp.Value;
                    callerOverrides[kvp.Key] = kvp.Value;
                }
                else
                {
                    callerOverrides['_' + kvp.Key] = kvp.Value;
                }
            }
        }

        // Extract required agents (resolved from variables)
        var requiredAgents = AgentResolver.ExtractAgentNames(watchItem, parameters);

        // Fleet-maintenance gate: a node being reverted/rebooted/updated/quarantined is not dispatchable.
        // Return a typed fleet-unavailable result rather than a bare 409.
        // TODO (run queue): this is the insertion point where a future queue would hold the request instead of failing.
        if (_maintenanceState is not null)
        {
            var unavailable = DispatchGate.GetMaintenanceBlockers(_maintenanceState, requiredAgents);
            if (unavailable is not null)
                return Conflict(new
                {
                    error = "fleet-unavailable",
                    message = $"{unavailable.BlockedNodes.Count} required agent(s) are in maintenance and cannot take work.",
                    blockedNodes = unavailable.BlockedNodes.Select(b => new { agent = b.NodeId, reason = b.Reason }),
                });
        }

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
        var pipelineLockToken = string.Empty;
        lock (tagLock)
        {
            if (_sessionManager.HasActiveExecution(watchItemTag))
                return Conflict(ApiErrorFactory.Conflict($"WatchItem '{watchItemTag}' is already running"));

            // Single-run gate: acquire the authoritative pipeline lock. ANY active lock
            // (any user, any role, owner included) blocks the trigger — the path is Cancel.
            if (_lockRegistry is not null)
            {
                var clientKind = string.Equals(source, "WPF", StringComparison.OrdinalIgnoreCase)
                    ? ClientKind.Wpf : ClientKind.Web;
                var owner = new OwnerIdentity(userId, displayName, clientKind);
                var acquire = _lockRegistry.TryAcquire(watchItemTag, owner, LockKind.Trigger);
                if (acquire is AcquireResult.Conflict pipelineConflict)
                    return Conflict(new { error = "pipeline-locked", @lock = LockMapper.ToDto(pipelineConflict.ExistingLock) });
                if (acquire is AcquireResult.Success pipelineSuccess)
                    pipelineLockToken = pipelineSuccess.Lock.Token;
            }

            // Try to lock all agents atomically
            sessionId = Guid.NewGuid().ToString("N")[..12];
            if (requiredAgents.Count > 0)
            {
                var (locked, conflicts) = _lockManager.TryLockAgents(
                    requiredAgents, sessionId, watchItemTag, userId, source);

                if (!locked)
                {
                    // Free the single-run lock we just took so the pipeline isn't stuck.
                    if (_lockRegistry is not null && pipelineLockToken.Length > 0)
                        _lockRegistry.TryRelease(watchItemTag, pipelineLockToken);
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
            session.UserDisplayName = displayName;
            session.UserRole = role;
            session.OwnerSid = _ownershipChecker.GetUserSid(HttpContext.User);
            session.Source = source;
            session.LockedAgents = requiredAgents.ToArray();
        }

        // Persist the caller's values to EVERY parameter file this pipeline initialises. Writing only the
        // first left later stages (e.g. Sanity after Warm) running the previous build. Each file keeps its
        // own contents; only the caller-supplied keys are overlaid. Missing paths are skipped rather than
        // created, so a mistyped ParameterFile never silently becomes a real one.
        if (callerOverrides.Count > 0)
        {
            foreach (var file in paramFiles)
            {
                if (!System.IO.File.Exists(file))
                    continue;

                try
                {
                    var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var order = new List<string>();
                    foreach (var (key, value) in ParameterResolver.ParseParameterFile(file))
                    {
                        if (own.TryAdd(key, value)) order.Add(key);
                        else own[key] = value;
                    }
                    foreach (var (key, value) in callerOverrides)
                    {
                        if (own.TryAdd(key, value)) order.Add(key);
                        else own[key] = value;
                    }

                    var lines = order.Where(k => k.StartsWith('_')).Select(k => $"{k},{own[k]}");
                    var tempFile = file + ".tmp";
                    await System.IO.File.WriteAllLinesAsync(tempFile, lines);
                    System.IO.File.Move(tempFile, file, overwrite: true);
                }
                catch { /* best effort */ }
            }
        }

        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = watchItem.Path,
            SessionId = sessionId,
            StartedUtc = DateTime.UtcNow,
            Parameters = parameters,
            LockToken = pipelineLockToken,
        };

        // Bridge the client request id (X-Request-Id, also visible in the browser
        // console + apiFetch logs) to the canonical run id (sessionId) so a single
        // grep ties the WebClient action to the whole server/agent trace. runId is
        // the sessionId — the same id SignalR live logs and agent gRPC events carry.
        var clientCorr = HttpContext.Items["CorrelationId"] as string
            ?? HttpContext.Request.Headers["X-Request-Id"].FirstOrDefault()
            ?? "n/a";
        _appLogger.LogStructured(
            Microsoft.Extensions.Logging.LogLevel.Information,
            category: "Execution",
            message: $"\u25b6 Triggered '{watchItemTag}' (event {evt.Type}) by {displayName} [req {clientCorr}]",
            agent: null,
            runId: sessionId,
            pipeline: watchItemTag,
            action: "Trigger");

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

                _events.Publish(new ExecutionCompletedEvent(sessionId, watchItemTag, finalState,
                    completedSession?.SucceededCount ?? 0, completedSession?.FailedCount ?? 0, completedSession?.TotalActions ?? 0));

                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = finalState,
                    passed = completedSession?.SucceededCount ?? 0,
                    failed = completedSession?.FailedCount ?? 0,
                    total = completedSession?.TotalActions ?? 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    owner = new
                    {
                        userId = completedSession?.UserId ?? "",
                        displayName = completedSession?.UserDisplayName ?? completedSession?.UserId ?? "",
                        role = completedSession?.UserRole ?? "",
                    },
                }, new CancellationTokenSource(BroadcastTimeout).Token);
            }
            catch (OperationCanceledException)
            {
                _events.Publish(new ExecutionCompletedEvent(sessionId, watchItemTag, "Cancelled", 0, 0, 0));

                var cancelledSession = _sessionManager.GetSession(sessionId)
                    ?? _sessionManager.GetLastSession(watchItemTag);
                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = "Cancelled",
                    passed = 0, failed = 0, total = 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    owner = new
                    {
                        userId = cancelledSession?.UserId ?? "",
                        displayName = cancelledSession?.UserDisplayName ?? cancelledSession?.UserId ?? "",
                        role = cancelledSession?.UserRole ?? "",
                    },
                }, new CancellationTokenSource(BroadcastTimeout).Token);
            }
            catch (Exception ex)
            {
                _events.Publish(new ExecutionCompletedEvent(sessionId, watchItemTag, "Failed", 0, 0, 0));

                var failedSession = _sessionManager.GetSession(sessionId)
                    ?? _sessionManager.GetLastSession(watchItemTag);
                await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
                {
                    sessionId,
                    watchItemTag,
                    state = "Failed",
                    error = ex.Message,
                    passed = 0, failed = 0, total = 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    owner = new
                    {
                        userId = failedSession?.UserId ?? "",
                        displayName = failedSession?.UserDisplayName ?? failedSession?.UserId ?? "",
                        role = failedSession?.UserRole ?? "",
                    },
                }, new CancellationTokenSource(BroadcastTimeout).Token);
            }
            finally
            {
                // Release locks only if not already released (e.g., by cancel endpoint)
                var released = _lockManager.ReleaseSession(sessionId);
                if (released > 0)
                    BroadcastLockChange("Pipeline completed");
            }
        });

        // Notify both SignalR clients and in-process subscribers (FleetVM).
        // The SignalR "ExecutionStarted" broadcast (including pendingActions,
        // userId, and lockedAgents) is emitted by SignalRNotifier.OnExecutionStarted
        // in response to this event — do NOT also send it directly here, or the
        // second (pendingActions-less) message overwrites the pre-populated pipeline
        // pills in the WebClient, leaving only completed actions visible.
        _events.Publish(new ExecutionStartedEvent(sessionId, watchItemTag, evt.Type, source));

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

        try
        {
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
        catch (System.IO.DirectoryNotFoundException)
        {
            return NotFound(ApiErrorFactory.NotFound($"Path not found: {basePath}"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            // Directory.Exists would report a UNC share the process identity cannot read as simply
            // "not found", which sends people looking for a bad path instead of a bad identity.
            return StatusCode(502, new
            {
                error = $"Cannot read build path: {basePath}",
                detail = ex.Message,
                hint = "The server process identity may lack access to this share.",
            });
        }
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
            return NotFound(ApiErrorFactory.InvalidTag(watchItemTag));

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
        var session = _sessionManager.GetSession(sessionId);
        if (session == null)
            return NotFound(ApiErrorFactory.NotFound($"Session '{sessionId}' not found"));

        if (!_ownershipChecker.CanAccessSession(HttpContext.User, session))
        {
            return StatusCode(403, ApiErrorFactory.Forbidden("Cannot access another user's session"));
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

    /// <summary>POST /api/execution/cancel – cancel all running sessions.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelAll()
    {
        var userId = HttpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "Unknown";

        // P4-1: cancelling ALL sessions requires Pipeline_CancelAll in Secured mode.
        if (!await IsRbacAuthorizedAsync(Permission.Pipeline_CancelAll, null, HttpContext.RequestAborted))
            return StatusCode(403, ApiErrorFactory.Forbidden("Not authorized to cancel all executions."));

        // Snapshot active sessions (ID + tag) before cancellation moves them to history
        var activeSessions = _sessionManager.GetActiveSessions()
            .Select(s => new { s.SessionId, s.WatchItemTag }).ToList();

        var cancelledTags = _sessionManager.CancelAll();

        _appLogger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, "Audit",
            $"CancelAll: actor={userId}, source={source}, cancelled={cancelledTags.Count} session(s): [{string.Join(", ", activeSessions.Select(s => s.SessionId))}]");

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
            }, new CancellationTokenSource(BroadcastTimeout).Token);
        }

        if (cancelledTags.Count > 0)
            BroadcastLockChange("All sessions cancelled");

        return Ok(new
        {
            message = $"Cancelled {cancelledTags.Count} execution(s).",
            activeExecutions = _sessionManager.ActiveExecutionCount,
        });
    }

    /// <summary>POST /api/execution/{sessionId}/cancel — cancel a specific session (ownership enforced).</summary>
    [HttpPost("{sessionId}/cancel")]
    public async Task<IActionResult> CancelSession(string sessionId)
    {
        var userId = _ownershipChecker.GetUserSid(HttpContext.User);
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "WebClient";

        var session = _sessionManager.GetSession(sessionId);
        if (session == null || session.State != SessionState.Running)
            return NotFound(ApiErrorFactory.NotFound($"Session '{sessionId}' not found or not running"));

        // Authorization: the session owner may always cancel their own run. Elevated
        // users (Admin + Senior Manager) may cancel ANY user's run. CanAccessSession
        // covers owner + legacy Admin; the RBAC Pipeline_CancelAll check additionally
        // admits Senior Managers (and Administrators) in Secured mode.
        if (!_ownershipChecker.CanAccessSession(HttpContext.User, session)
            && !await CanCancelOthersAsync(session.WatchItemTag, HttpContext.RequestAborted))
        {
            _auditLogger.LogAuthorization(userId, "CancelSession", sessionId, "Denied");
            return StatusCode(403, ApiErrorFactory.Forbidden("Cannot cancel another user's session"));
        }

        var cancelled = _sessionManager.CancelSession(sessionId);
        if (!cancelled) return NotFound(ApiErrorFactory.NotFound($"Session '{sessionId}' not found or not running"));

        _appLogger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, "Audit",
            $"CancelSession: actor={userId}, source={source}, sessionId={sessionId}, pipeline={session.WatchItemTag}");

        _lockManager.ReleaseSession(sessionId);
        BroadcastLockChange($"Session {sessionId} cancelled");

        await _hub.Clients.Group("global").SendAsync("ExecutionCancelled", new
        {
            sessionId,
            timestamp = DateTime.UtcNow.ToString("o"),
        }, new CancellationTokenSource(BroadcastTimeout).Token);

        return Ok(new { message = "Cancellation requested", cancelledBy = userId });
    }

    /// <summary>
    /// Returns true if the current caller is permitted to cancel ANOTHER user's run.
    /// Maps to the RBAC <see cref="Permission.Pipeline_CancelAll"/> grant, which is held
    /// only by Administrators and Senior Managers. Resolves the RBAC services lazily from
    /// the request scope so hosts without RBAC wired (e.g. feed-only or minimal test hosts)
    /// simply fall back to legacy ownership rules. Returns false (deny) on any gap.
    /// </summary>
    private async Task<bool> CanCancelOthersAsync(string pipelineId, CancellationToken ct)
    {
        var authzService = HttpContext.RequestServices
            .GetService(typeof(TestControllerGrpc.Authorization.IAuthorizationService))
            as TestControllerGrpc.Authorization.IAuthorizationService;
        var authInterceptor = HttpContext.RequestServices
            .GetService(typeof(SessionAuthInterceptor)) as SessionAuthInterceptor;
        if (authzService is null || authInterceptor is null)
            return false;

        var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault();
        var user = await authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
        if (user is null)
            return false;

        var decision = await authzService.CanAsync(user, Permission.Pipeline_CancelAll, pipelineId, ct);
        return decision.Allowed;
    }

    /// <summary>
    /// P4-1: enforces a fine-grained RBAC permission on a mutating execution action.
    /// Fails OPEN when RBAC is disabled (Default mode) or its services are not wired,
    /// preserving legacy behaviour; enforces <c>CanAsync</c> only in Secured mode.
    /// </summary>
    private Task<bool> IsRbacAuthorizedAsync(Permission permission, string? resourceId, CancellationToken ct)
        => RbacGate.IsAuthorizedAsync(HttpContext, permission, resourceId, ct);

    // ?? Force-release endpoints (admin only) ?????????????????????????

    /// <summary>POST /api/execution/force-release/{agentName} — admin force-release a single agent.</summary>
    [HttpPost("force-release/{agentName}")]
    [Authorize(Policy = SecurityPolicies.Admin)]
    public async Task<IActionResult> ForceReleaseAgent(string agentName)
    {
        var userId = _ownershipChecker.GetUserSid(HttpContext.User);
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "Unknown";

        // P4-1: in Secured mode also require the fine-grained Pipeline_ForceRelease
        // permission (in addition to the coarse Admin policy) for a clean audit trail.
        if (!await IsRbacAuthorizedAsync(Permission.Pipeline_ForceRelease, agentName, HttpContext.RequestAborted))
            return StatusCode(403, ApiErrorFactory.Forbidden($"Not authorized to force-release agent '{agentName}'."));

        var currentLock = _lockManager.GetLock(agentName);
        if (currentLock == null)
            return NotFound(ApiErrorFactory.NotFound($"Agent '{agentName}' is not locked"));

        _lockManager.ForceRelease(agentName);

        _auditLogger.LogAdminAction(userId, "ForceReleaseAgent", agentName,
            $"previousSession={currentLock.SessionId}, previousUser={currentLock.UserId}");
        _appLogger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, "Audit",
            $"ForceReleaseAgent: actor={userId}, source={source}, agent={agentName}, " +
            $"previousSession={currentLock.SessionId}, previousPipeline={currentLock.WatchItemTag}, previousUser={currentLock.UserId}");

        BroadcastLockChange($"Force-released: {agentName}");

        return Ok(new
        {
            message = $"Agent '{agentName}' force-released",
            previousLock = new { currentLock.SessionId, currentLock.UserId, currentLock.WatchItemTag },
        });
    }

    /// <summary>POST /api/execution/force-release-all — admin emergency release all locks.</summary>
    [HttpPost("force-release-all")]
    [Authorize(Policy = SecurityPolicies.Admin)]
    public async Task<IActionResult> ForceReleaseAll()
    {
        var userId = _ownershipChecker.GetUserSid(HttpContext.User);
        var source = HttpContext.Request.Headers["X-Source"].FirstOrDefault() ?? "Unknown";

        // P4-1: in Secured mode also require the fine-grained Pipeline_ForceRelease permission.
        if (!await IsRbacAuthorizedAsync(Permission.Pipeline_ForceRelease, null, HttpContext.RequestAborted))
            return StatusCode(403, ApiErrorFactory.Forbidden("Not authorized to force-release all locks."));

        var count = _lockManager.ForceReleaseAll();

        _auditLogger.LogAdminAction(userId, "ForceReleaseAll", "all-agents", $"releasedCount={count}");
        _appLogger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, "Audit",
            $"ForceReleaseAll: actor={userId}, source={source}, releasedCount={count}");

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

        // Publish to event aggregator so WPF FleetVM (and SignalRNotifier) both receive it.
        // Previously only called _notifier directly, which sent to SignalR but never
        // notified the in-process FleetVM — causing agent cards to stay green.
        _events.Publish(new AgentLocksChangedEvent
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
        completedUtc = s.CompletedUtc?.ToString("o"),
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
        agents = BuildAgentDtos(s),
    };

    /// <summary>
    /// Merges started/completed actions (from AgentSummaries) with not-yet-started
    /// actions (from SnapshotNodes) so the WebClient renders the full pipeline scope
    /// the moment a run is triggered, instead of revealing pills one-by-one.
    /// </summary>
    private static object[] BuildAgentDtos(ExecutionSession s)
    {
        var summaries = s.GetAgentSummaries();
        var startedTags = new HashSet<string>(
            summaries.SelectMany(a => a.Actions.Select(act => act.ActionTag)),
            StringComparer.OrdinalIgnoreCase);

        var pendingByAgent = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        CollectPendingFromSnapshot(s.SnapshotNodes, s.ResolvedParameters, startedTags, pendingByAgent);

        var result = new List<object>();
        var processedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in summaries)
        {
            processedAgents.Add(a.AgentName);
            var actions = a.Actions.Select(act => (object)new
            {
                tag = act.ActionTag,
                actionType = act.ActionType,
                agentName = act.AgentName,
                command = act.Command,
                status = MapActionStatus(act.Outcome),
                exitCode = act.ExitCode,
                errorMessage = act.ErrorMessage,
                duration = act.Duration.ToString(@"mm\:ss"),
                startedUtc = act.StartedUtc.ToString("o"),
                durationSeconds = (int)act.Duration.TotalSeconds,
            }).ToList();

            if (pendingByAgent.TryGetValue(a.AgentName, out var pending))
                actions.AddRange(pending);

            result.Add(new
            {
                agentName = a.AgentName,
                status = a.Status,
                completedCount = a.CompletedCount,
                totalCount = actions.Count,
                progressPercent = actions.Count > 0
                    ? (int)((double)a.CompletedCount / actions.Count * 100)
                    : 0,
                actions,
            });
        }

        foreach (var (agentName, pending) in pendingByAgent)
        {
            if (processedAgents.Contains(agentName)) continue;
            result.Add(new
            {
                agentName,
                status = "Idle",
                completedCount = 0,
                totalCount = pending.Count,
                progressPercent = 0,
                actions = pending,
            });
        }

        return result.ToArray();
    }

    private static void CollectPendingFromSnapshot(
        IReadOnlyList<IActionNode> nodes,
        IReadOnlyDictionary<string, string>? parameters,
        HashSet<string> startedTags,
        Dictionary<string, List<object>> pendingByAgent)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig action:
                    if (startedTags.Contains(action.ResolvedTag)) break;
                    var agent = ResolveAgentForApi(action.AgentName, parameters);
                    if (!pendingByAgent.TryGetValue(agent, out var list))
                    {
                        list = new List<object>();
                        pendingByAgent[agent] = list;
                    }
                    list.Add(new
                    {
                        tag = action.ResolvedTag,
                        actionType = action.Type.ToString(),
                        agentName = agent,
                        command = action.Command,
                        status = "Pending",
                        exitCode = (int?)null,
                        errorMessage = (string?)null,
                        duration = (string?)null,
                        startedUtc = (string?)null,
                        durationSeconds = 0,
                    });
                    break;

                case ActionGroupConfig group:
                    CollectPendingFromSnapshot(group.Children, parameters, startedTags, pendingByAgent);
                    break;
            }
        }
    }

    private static string ResolveAgentForApi(string agentName, IReadOnlyDictionary<string, string>? parameters)
    {
        if (string.IsNullOrEmpty(agentName)) return "Controller";
        if (parameters != null && agentName.StartsWith('[') && agentName.EndsWith(']'))
        {
            var varName = agentName[1..^1];
            if (parameters.TryGetValue(varName, out var resolved)) return resolved;
            if (varName.StartsWith('_') && parameters.TryGetValue(varName[1..], out var resolved2)) return resolved2;
        }
        return agentName;
    }
}
