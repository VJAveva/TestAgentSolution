using System.Text.Json;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class ExecutionEndpoints
{
    public static RouteGroupBuilder MapExecutionEndpoints(this RouteGroupBuilder group)
    {
        // These endpoints extend the shared ExecutionController with standalone-specific features.
        // Common endpoints (sessions, status, trigger/{tag}, cancel) are provided by the shared library.
        group.MapPost("/trigger-all", TriggerAll);
        group.MapPost("/trigger-event/{tag}/{eventIndex:int}", TriggerEvent);
        group.MapPost("/retry/{sessionId}", RetrySession);
        group.MapPost("/preflight/{tag}", PreflightCheck);

        // Proxy-aware overrides: when a WPF controller is running alongside this
        // WebApi, its embedded API (ControllerProxyUrl) has the real session data.
        // These endpoints merge local and proxied data so the WebClient dashboard
        // always shows execution progress regardless of which host triggered it.
        group.MapGet("/proxy/dashboard-sessions", ProxyDashboardSessions);
        group.MapGet("/proxy/status", ProxyExecutionStatus);
        group.MapGet("/proxy/logs/{sessionId}", ProxySessionLogs);
        return group;
    }

    /// <summary>POST /api/execution/trigger-all � trigger all WatchItems.</summary>
    private static IResult TriggerAll(
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier,
        IAppLogger logger)
    {
        WatchListConfig config;
        try
        {
            config = fileService.Load();
        }
        catch (Exception ex)
        {
            logger.Error("Execution", "Failed to load WatchList for trigger-all", ex);
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }

        var triggered = new List<string>();

        foreach (var wi in config.WatchItems.Where(w => w.IsEnabled))
        {
            if (sessionManager.HasActiveExecution(wi.Tag))
                continue;

            var session = sessionManager.BeginSession(
                wi.Tag,
                "Manual",
                new Dictionary<string, string>(),
                wi.Events.SelectMany(e => e.Children).ToList());

            triggered.Add(wi.Tag);

            // Notify SignalR clients
            notifier.NotifyActionProgress(new { nodeTag = wi.Tag, status = "Running", timestamp = DateTime.UtcNow.ToString("o") });
            notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
                $"Triggered WatchItem '{wi.Tag}' (session {session.SessionId})"));
        }

        logger.Info("Execution", $"Trigger-all: triggered {triggered.Count} WatchItem(s)");
        return Results.Ok(new
        {
            message = $"Triggered {triggered.Count} WatchItem(s).",
            triggered,
            activeExecutions = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>POST /api/execution/trigger/{tag} � trigger specific WatchItem.</summary>
    private static IResult TriggerByTag(
        string tag,
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier,
        IAppLogger logger)
    {
        WatchListConfig config;
        try
        {
            config = fileService.Load();
        }
        catch (Exception ex)
        {
            logger.Error("Execution", $"Failed to load WatchList for trigger '{tag}'", ex);
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }

        var wi = config.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));

        if (wi is null)
            return Results.NotFound($"WatchItem '{tag}' not found.");

        if (sessionManager.HasActiveExecution(tag))
            return Results.Conflict($"WatchItem '{tag}' is already executing.");

        var session = sessionManager.BeginSession(
            wi.Tag,
            "Manual",
            new Dictionary<string, string>(),
            wi.Events.SelectMany(e => e.Children).ToList());

        notifier.NotifyActionProgress(new { nodeTag = wi.Tag, status = "Running", timestamp = DateTime.UtcNow.ToString("o") });
        notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
            $"Triggered WatchItem '{wi.Tag}' (session {session.SessionId})"));

        logger.Info("Execution", $"Triggered WatchItem '{wi.Tag}' (session {session.SessionId})");
        return Results.Ok(new
        {
            message = $"Triggered '{wi.Tag}'.",
            sessionId = session.SessionId
        });
    }

    /// <summary>POST /api/execution/trigger-event/{tag}/{eventIndex} � trigger specific event.</summary>
    private static IResult TriggerEvent(
        string tag,
        int eventIndex,
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier,
        IAppLogger logger)
    {
        WatchListConfig config;
        try
        {
            config = fileService.Load();
        }
        catch (Exception ex)
        {
            logger.Error("Execution", $"Failed to load WatchList for trigger-event '{tag}'", ex);
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }

        var wi = config.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));

        if (wi is null)
            return Results.NotFound($"WatchItem '{tag}' not found.");

        if (eventIndex < 0 || eventIndex >= wi.Events.Count)
            return Results.BadRequest($"Event index {eventIndex} out of range (0..{wi.Events.Count - 1}).");

        var ev = wi.Events[eventIndex];
        var session = sessionManager.BeginSession(
            wi.Tag,
            ev.Type,
            new Dictionary<string, string>(),
            ev.Children);

        notifier.NotifyActionProgress(new { nodeTag = wi.Tag, status = "Running", timestamp = DateTime.UtcNow.ToString("o") });
        notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
            $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type}) (session {session.SessionId})"));

        logger.Info("Execution", $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type})");
        return Results.Ok(new
        {
            message = $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type}).",
            sessionId = session.SessionId
        });
    }

    /// <summary>POST /api/execution/cancel � cancel all running executions.</summary>
    private static IResult CancelAll(
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier)
    {
        var cancelledTags = sessionManager.CancelAll();

        foreach (var tag in cancelledTags)
            notifier.NotifyActionProgress(new { nodeTag = tag, status = "Idle", timestamp = DateTime.UtcNow.ToString("o") });

        notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
            $"Cancelled {cancelledTags.Count} active execution(s)."));

        return Results.Ok(new
        {
            message = $"Cancelled {cancelledTags.Count} execution(s).",
            activeExecutions = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>POST /api/execution/cancel/{sessionId} � cancel a specific session.</summary>
    private static IResult CancelBySession(
        string sessionId,
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier)
    {
        var cancelled = sessionManager.CancelSession(sessionId);
        if (!cancelled)
            return Results.NotFound($"Session '{sessionId}' not found or already completed.");

        notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
            $"Session '{sessionId}' cancelled."));

        return Results.Ok(new
        {
            message = $"Session '{sessionId}' cancelled.",
            sessionId,
            activeExecutions = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>POST /api/execution/retry/{sessionId} — retry failed actions from a session.</summary>
    private static IResult RetrySession(
        string sessionId,
        ExecutionSessionManager sessionManager,
        IRealtimeNotifier notifier)
    {
        var retryable = sessionManager.GetRetryableNodes(sessionId);
        if (retryable.Count == 0)
            return Results.NotFound($"No retryable actions found for session '{sessionId}'.");

        notifier.NotifyLogEntry(new PipelineLogEntry(DateTime.Now, "Execution",
            $"Retry requested for session '{sessionId}' — {retryable.Count} action(s)"));

        return Results.Ok(new
        {
            message = $"Retry initiated for {retryable.Count} failed action(s).",
            sessionId,
            retryableCount = retryable.Count
        });
    }

    /// <summary>
    /// POST /api/execution/preflight/{tag} — verify all required agents are online
    /// and not locked before triggering execution. Returns 409 Conflict if blocked.
    /// </summary>
    private static async Task<IResult> PreflightCheck(
        string tag,
        WatchListFileService fileService,
        AgentRegistry registry,
        AgentGrpcClientManager grpcManager,
        AgentLockManager lockManager,
        IAppLogger logger)
    {
        WatchListConfig config;
        try
        {
            config = fileService.Load();
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }

        var wi = config.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));

        if (wi is null)
            return Results.NotFound($"WatchItem '{tag}' not found.");

        // Collect all unique agent names from the pipeline's action tree
        var requiredAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAgentNames(wi.Events.SelectMany(e => e.Children).ToList(), requiredAgents);

        // Remove "Controller" (local) — only remote agents need checking
        requiredAgents.Remove("Controller");
        requiredAgents.Remove("");

        var issues = new List<object>();
        var locks = lockManager.GetAllLocks();

        foreach (var agentName in requiredAgents)
        {
            // Check if registered
            if (!registry.TryGet(agentName, out var entry))
            {
                issues.Add(new { agent = agentName, reason = "NotRegistered" });
                continue;
            }

            // Check if locked
            var agentLock = locks.FirstOrDefault(l =>
                string.Equals(l.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
            if (agentLock != null)
            {
                issues.Add(new { agent = agentName, reason = "Locked", lockedBy = agentLock.SessionId });
                continue;
            }

            // Check if reachable (quick gRPC ping)
            try
            {
                var client = grpcManager.GetClient(entry.Address);
                await client.GetStateAsync(new Google.Protobuf.WellKnownTypes.Empty(),
                    deadline: DateTime.UtcNow.AddSeconds(5));
            }
            catch
            {
                issues.Add(new { agent = agentName, reason = "Offline" });
            }
        }

        if (issues.Count > 0)
        {
            logger.Warn("Execution", $"Preflight failed for '{tag}': {issues.Count} agent(s) unavailable");
            return Results.Conflict(new
            {
                tag,
                ready = false,
                issues,
                message = $"Cannot trigger '{tag}': {issues.Count} required agent(s) not available."
            });
        }

        return Results.Ok(new
        {
            tag,
            ready = true,
            requiredAgents = requiredAgents.ToList(),
            message = $"All {requiredAgents.Count} required agent(s) are online and available."
        });
    }

    private static void CollectAgentNames(IReadOnlyList<IActionNode> nodes, HashSet<string> agents)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig action:
                    if (!string.IsNullOrEmpty(action.AgentName))
                        agents.Add(action.AgentName);
                    break;
                case ActionGroupConfig group:
                    CollectAgentNames(group.Children, agents);
                    break;
            }
        }
    }

    /// <summary>GET /api/execution/status � current execution state overview.</summary>
    private static IResult GetStatus(ExecutionSessionManager sessionManager)
    {
        return Results.Ok(new
        {
            isExecuting = sessionManager.HasAnyActiveExecution,
            activeCount = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>GET /api/execution/sessions � active sessions with per-session detail.</summary>
    private static IResult GetSessions(ExecutionSessionManager sessionManager)
    {
        var active = sessionManager.GetActiveSessions();
        return Results.Ok(new
        {
            activeCount = sessionManager.ActiveExecutionCount,
            hasActive = sessionManager.HasAnyActiveExecution,
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
            })
        });
    }

    // ── Proxy-aware endpoints ──────────────────────────────────────────

    /// <summary>
    /// GET /api/execution/proxy/dashboard-sessions
    /// Merges local sessions with the WPF controller's sessions (if configured).
    /// The WebClient should call this instead of the shared dashboard-sessions
    /// endpoint when a WPF controller may be running pipelines.
    /// </summary>
    private static async Task<IResult> ProxyDashboardSessions(
        ExecutionSessionManager sessionManager,
        ControllerProxyService proxy)
    {
        // Local sessions from this WebApi's own session manager
        var localActive = sessionManager.GetActiveSessions();
        var localHistory = sessionManager.GetHistory(20);
        var localActiveIds = new HashSet<string>(localActive.Select(s => s.SessionId));
        var localHistoryIds = new HashSet<string>(localHistory.Select(s => s.SessionId));

        // Try to get controller's sessions
        var proxied = await proxy.GetDashboardSessionsAsync();

        if (proxied is null)
        {
            // Controller unavailable — return local data only (same as shared endpoint)
            return Results.Ok(new
            {
                active = localActive.Select(MapSession),
                history = localHistory.Select(MapSession),
            });
        }

        // Merge: proxied data takes priority (it has the real execution progress),
        // but include any local sessions not in the controller's data.
        var mergedActive = new List<object>();
        var mergedHistory = new List<object>();

        // Add all proxied sessions (these have the real progress data)
        foreach (var s in proxied.Active) mergedActive.Add(s);
        foreach (var s in proxied.History) mergedHistory.Add(s);

        // Add local sessions that aren't already in the proxied data
        // (sessions triggered directly via the standalone WebApi)
        foreach (var s in localActive)
        {
            var id = s.SessionId;
            if (!proxied.Active.Any(p => HasMatchingSessionId(p, id)))
                mergedActive.Add(MapSession(s));
        }
        foreach (var s in localHistory)
        {
            var id = s.SessionId;
            if (!proxied.History.Any(p => HasMatchingSessionId(p, id)))
                mergedHistory.Add(MapSession(s));
        }

        return Results.Ok(new
        {
            active = mergedActive,
            history = mergedHistory,
            source = "merged",
        });
    }

    /// <summary>
    /// GET /api/execution/proxy/status
    /// Returns combined execution status from local + controller.
    /// </summary>
    private static async Task<IResult> ProxyExecutionStatus(
        ExecutionSessionManager sessionManager,
        ControllerProxyService proxy)
    {
        var localExecuting = sessionManager.HasAnyActiveExecution;
        var localCount = sessionManager.ActiveExecutionCount;

        var proxied = await proxy.GetExecutionStatusAsync();

        return Results.Ok(new
        {
            isExecuting = localExecuting || (proxied?.IsExecuting ?? false),
            activeCount = localCount + (proxied?.ActiveCount ?? 0),
            source = proxied is not null ? "merged" : "local",
        });
    }

    /// <summary>
    /// GET /api/execution/proxy/logs/{sessionId}
    /// Fetches recent log entries for a session from the WPF controller.
    /// </summary>
    private static async Task<IResult> ProxySessionLogs(
        string sessionId,
        ControllerProxyService proxy)
    {
        var logs = await proxy.GetRecentLogsAsync(sessionId);
        if (logs is null)
            return Results.Ok(new { sessionId, logs = Array.Empty<object>(), count = 0 });

        return Results.Ok(logs.Value);
    }

    private static bool HasMatchingSessionId(JsonElement element, string sessionId)
    {
        return element.TryGetProperty("sessionId", out var prop)
            && prop.GetString() == sessionId;
    }

    private static object MapSession(ExecutionSession s) => new
    {
        sessionId = s.SessionId,
        watchItemTag = s.WatchItemTag,
        userId = s.UserId,
        source = s.Source,
        status = s.State switch
        {
            SessionState.Running => "Running",
            SessionState.Completed => "Success",
            SessionState.PartialFailure => "PartialFailure",
            SessionState.Failed => "Failed",
            _ => "Running",
        },
        startedUtc = s.StartedUtc.ToString("o"),
        elapsed = (DateTime.UtcNow - s.StartedUtc).ToString(@"hh\:mm\:ss"),
        lockedAgents = s.LockedAgents,
        buildNumber = s.ResolvedParameters
            .GetValueOrDefault("_BuildNumber", ""),
        totalActions = CountLeafActions(s.SnapshotNodes),
        completedActions = s.ActionResults.Count,
        passedActions = s.SucceededCount,
        failedActions = s.FailedCount,
        progressPercent = CountLeafActions(s.SnapshotNodes) > 0
            ? (int)((double)s.ActionResults.Count / CountLeafActions(s.SnapshotNodes) * 100)
            : 0,
        agents = BuildAgentDtos(s),
    };

    /// <summary>
    /// Merges pending actions (from SnapshotNodes) with completed/running actions
    /// (from AgentSummaries) so the full pipeline scope is always visible.
    /// </summary>
    private static object[] BuildAgentDtos(ExecutionSession s)
    {
        var summaries = s.GetAgentSummaries();
        var startedTags = new HashSet<string>(
            summaries.SelectMany(a => a.Actions.Select(act => act.ActionTag)),
            StringComparer.OrdinalIgnoreCase);

        // Collect pending actions from snapshot that haven't started yet
        var pendingByAgent = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        CollectPendingFromSnapshot(s.SnapshotNodes, s.ResolvedParameters, startedTags, pendingByAgent);

        // Build the result: started agents + pending pills appended
        var result = new List<object>();
        var processedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in summaries)
        {
            processedAgents.Add(a.AgentName);
            var startedActions = a.Actions.Select(act => new
            {
                tag = act.ActionTag,
                actionType = act.ActionType,
                agentName = act.AgentName,
                command = act.Command,
                status = act.Outcome switch
                {
                    ActionOutcome.Success => "Success",
                    ActionOutcome.Failed => "Failed",
                    ActionOutcome.Terminated => "Failed",
                    ActionOutcome.TimedOut => "Failed",
                    _ => "Running",
                },
                exitCode = act.ExitCode,
                errorMessage = act.ErrorMessage,
                duration = act.Duration.ToString(@"mm\:ss"),
            }).Cast<object>().ToList();

            // Append pending actions that haven't started for this agent
            if (pendingByAgent.TryGetValue(a.AgentName, out var pending))
                startedActions.AddRange(pending);

            result.Add(new
            {
                agentName = a.AgentName,
                status = a.Status,
                completedCount = a.CompletedCount,
                totalCount = startedActions.Count,
                progressPercent = startedActions.Count > 0
                    ? (int)((double)a.CompletedCount / startedActions.Count * 100)
                    : 0,
                actions = startedActions,
            });
        }

        // Add agents that have only pending actions (not yet started)
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
        Dictionary<string, string>? parameters,
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
                    });
                    break;

                case ActionGroupConfig group:
                    CollectPendingFromSnapshot(group.Children, parameters, startedTags, pendingByAgent);
                    break;
            }
        }
    }

    private static string ResolveAgentForApi(string agentName, Dictionary<string, string>? parameters)
    {
        if (string.IsNullOrEmpty(agentName)) return "Controller";
        if (parameters != null && agentName.StartsWith('[') && agentName.EndsWith(']'))
        {
            var varName = agentName[1..^1];
            if (parameters.TryGetValue(varName, out var resolved)) return resolved;
            if (varName.StartsWith('_') && parameters.TryGetValue(varName[1..], out resolved)) return resolved;
        }
        return agentName;
    }

    private static int CountLeafActions(IReadOnlyList<IActionNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig: count++; break;
                case ActionGroupConfig g: count += CountLeafActions(g.Children); break;
            }
        }
        return count;
    }
}
