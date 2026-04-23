using Microsoft.AspNetCore.SignalR;
using TestController.WebApi.Hubs;
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
        return group;
    }

    /// <summary>POST /api/execution/trigger-all — trigger all WatchItems.</summary>
    private static IResult TriggerAll(
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IHubContext<LiveHub> hub,
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
            hub.Clients.All.SendAsync("NodeProgress", wi.Tag, "Running");
            hub.Clients.All.SendAsync("ExecutionLog", new
            {
                Message = $"Triggered WatchItem '{wi.Tag}'",
                SessionId = session.SessionId,
                Timestamp = DateTime.UtcNow
            });
        }

        logger.Info("Execution", $"Trigger-all: triggered {triggered.Count} WatchItem(s)");
        return Results.Ok(new
        {
            message = $"Triggered {triggered.Count} WatchItem(s).",
            triggered,
            activeExecutions = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>POST /api/execution/trigger/{tag} — trigger specific WatchItem.</summary>
    private static IResult TriggerByTag(
        string tag,
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IHubContext<LiveHub> hub,
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

        hub.Clients.All.SendAsync("NodeProgress", wi.Tag, "Running");
        hub.Clients.All.SendAsync("ExecutionLog", new
        {
            Message = $"Triggered WatchItem '{wi.Tag}'",
            SessionId = session.SessionId,
            Timestamp = DateTime.UtcNow
        });

        logger.Info("Execution", $"Triggered WatchItem '{wi.Tag}' (session {session.SessionId})");
        return Results.Ok(new
        {
            message = $"Triggered '{wi.Tag}'.",
            sessionId = session.SessionId
        });
    }

    /// <summary>POST /api/execution/trigger-event/{tag}/{eventIndex} — trigger specific event.</summary>
    private static IResult TriggerEvent(
        string tag,
        int eventIndex,
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IHubContext<LiveHub> hub,
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

        hub.Clients.All.SendAsync("NodeProgress", wi.Tag, "Running");
        hub.Clients.All.SendAsync("ExecutionLog", new
        {
            Message = $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type})",
            SessionId = session.SessionId,
            Timestamp = DateTime.UtcNow
        });

        logger.Info("Execution", $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type})");
        return Results.Ok(new
        {
            message = $"Triggered '{wi.Tag}' event [{eventIndex}] ({ev.Type}).",
            sessionId = session.SessionId
        });
    }

    /// <summary>POST /api/execution/cancel — cancel all running executions.</summary>
    private static IResult CancelAll(
        ExecutionSessionManager sessionManager,
        IHubContext<LiveHub> hub)
    {
        var cancelledTags = sessionManager.CancelAll();

        foreach (var tag in cancelledTags)
            hub.Clients.All.SendAsync("NodeProgress", tag, "Idle");

        hub.Clients.All.SendAsync("ExecutionLog", new
        {
            Message = $"Cancelled {cancelledTags.Count} active execution(s).",
            Timestamp = DateTime.UtcNow
        });

        return Results.Ok(new
        {
            message = $"Cancelled {cancelledTags.Count} execution(s).",
            activeExecutions = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>POST /api/execution/cancel/{sessionId} — cancel a specific session.</summary>
    private static IResult CancelBySession(
        string sessionId,
        ExecutionSessionManager sessionManager,
        IHubContext<LiveHub> hub)
    {
        var cancelled = sessionManager.CancelSession(sessionId);
        if (!cancelled)
            return Results.NotFound($"Session '{sessionId}' not found or already completed.");

        hub.Clients.All.SendAsync("ExecutionLog", new
        {
            Message = $"Session '{sessionId}' cancelled.",
            SessionId = sessionId,
            Timestamp = DateTime.UtcNow
        });

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
        IHubContext<LiveHub> hub)
    {
        var retryable = sessionManager.GetRetryableNodes(sessionId);
        if (retryable.Count == 0)
            return Results.NotFound($"No retryable actions found for session '{sessionId}'.");

        hub.Clients.All.SendAsync("ExecutionLog", new
        {
            Message = $"Retry requested for session '{sessionId}' — {retryable.Count} action(s)",
            Timestamp = DateTime.UtcNow
        });

        return Results.Ok(new
        {
            message = $"Retry initiated for {retryable.Count} failed action(s).",
            sessionId,
            retryableCount = retryable.Count
        });
    }

    /// <summary>GET /api/execution/status — current execution state overview.</summary>
    private static IResult GetStatus(ExecutionSessionManager sessionManager)
    {
        return Results.Ok(new
        {
            isExecuting = sessionManager.HasAnyActiveExecution,
            activeCount = sessionManager.ActiveExecutionCount
        });
    }

    /// <summary>GET /api/execution/sessions — active sessions with per-session detail.</summary>
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
}
