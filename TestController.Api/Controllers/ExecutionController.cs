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
}

[ApiController]
[Route("api/execution")]
public class ExecutionController : ControllerBase
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IHubContext<ControllerHub> _hub;

    /// <summary>Per-tag locks to prevent TOCTOU race without serializing unrelated triggers.</summary>
    private static readonly ConcurrentDictionary<string, object> _triggerLocks = new(StringComparer.OrdinalIgnoreCase);

    public ExecutionController(
        ExecutionSessionManager sessionManager,
        IActionPipelineExecutor executor,
        IVocabularyMonitor vocabMonitor,
        IHubContext<ControllerHub> hub)
    {
        _sessionManager = sessionManager;
        _executor = executor;
        _vocabMonitor = vocabMonitor;
        _hub = hub;
    }

    /// <summary>GET /api/execution/sessions — list active sessions.</summary>
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

    /// <summary>GET /api/execution/status — current execution state overview.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            isExecuting = _sessionManager.HasAnyActiveExecution,
            activeCount = _sessionManager.ActiveExecutionCount,
        });
    }

    /// <summary>GET /api/execution/{sessionId} — detailed session info.</summary>
    [HttpGet("{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session == null) return NotFound();
        return Ok(ToSessionDto(session));
    }

    /// <summary>
    /// POST /api/execution/trigger/{watchItemTag}?eventType=Renamed — trigger a WatchItem.
    /// Accepts an optional JSON body with build number, drop location, and custom parameters.
    /// Uses a lock to prevent TOCTOU race between concurrent trigger requests.
    /// </summary>
    [HttpPost("trigger/{watchItemTag}")]
    public async Task<IActionResult> TriggerWatchItem(
        string watchItemTag,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TriggerRequest? request = null,
        [FromQuery] string? eventType = null)
    {
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

        // Atomic check-and-mark inside a per-tag lock to prevent TOCTOU race.
        var tagLock = _triggerLocks.GetOrAdd(watchItemTag, _ => new object());
        string sessionId;
        lock (tagLock)
        {
            if (_sessionManager.HasActiveExecution(watchItemTag))
                return Conflict(new { error = $"WatchItem '{watchItemTag}' is already running" });

            sessionId = Guid.NewGuid().ToString("N")[..12];
            _sessionManager.BeginSession(
                watchItemTag, evt.Type,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                evt.Children.ToList(),
                sessionId);
        }

        // Build parameters
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

        // Fire and forget — ExecuteEventTrackedAsync handles session lifecycle
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
        });

        await _hub.Clients.Group("global").SendAsync("ExecutionStarted", new
        {
            sessionId,
            watchItemTag,
            eventType = evt.Type,
            startTime = DateTime.UtcNow.ToString("o"),
            source = "WebClient",
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

    /// <summary>POST /api/execution/cancel — cancel all running sessions.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelAll()
    {
        var cancelledTags = _sessionManager.CancelAll();
        foreach (var tag in cancelledTags)
        {
            await _hub.Clients.Group("global").SendAsync("ExecutionCompleted", new
            {
                watchItemTag = tag,
                state = "Cancelled",
                timestamp = DateTime.UtcNow.ToString("o"),
            });
        }
        return Ok(new
        {
            message = $"Cancelled {cancelledTags.Count} execution(s).",
            activeExecutions = _sessionManager.ActiveExecutionCount,
        });
    }

    /// <summary>POST /api/execution/{sessionId}/cancel — cancel a specific session.</summary>
    [HttpPost("{sessionId}/cancel")]
    public async Task<IActionResult> CancelSession(string sessionId)
    {
        var cancelled = _sessionManager.CancelSession(sessionId);
        if (!cancelled) return NotFound(new { error = "Session not found or not running" });

        await _hub.Clients.Group("global").SendAsync("ExecutionCancelled", new
        {
            sessionId,
            timestamp = DateTime.UtcNow.ToString("o"),
        });

        return Ok(new { message = "Cancellation requested" });
    }

    private static object ToSessionDto(ExecutionSession s) => new
    {
        sessionId = s.SessionId,
        watchItemTag = s.WatchItemTag,
        eventType = s.EventType,
        startedUtc = s.StartedUtc,
        completedUtc = s.CompletedUtc,
        state = s.State.ToString(),
        totalActions = s.TotalActions,
        succeededCount = s.SucceededCount,
        failedCount = s.FailedCount,
        summary = s.SummaryText,
    };
}
