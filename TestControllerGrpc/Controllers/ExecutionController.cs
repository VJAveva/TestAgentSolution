using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using TestControllerGrpc.Hubs;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Controllers;

[ApiController]
[Route("api/execution")]
public class ExecutionController : ControllerBase
{
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IHubContext<ControllerHub> _hub;

    /// <summary>Lock to prevent TOCTOU race between HasActiveExecution check and trigger.</summary>
    private static readonly object _triggerLock = new();

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

    /// <summary>GET /api/execution/sessions — list active and recent sessions.</summary>
    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        var active = _sessionManager.GetActiveSessions()
            .Select(ToSessionDto);
        var history = _sessionManager.GetHistory(20)
            .Select(ToSessionDto);
        return Ok(new { active, history });
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
    /// Uses a lock to prevent TOCTOU race between concurrent trigger requests.
    /// </summary>
    [HttpPost("trigger/{watchItemTag}")]
    public async Task<IActionResult> TriggerWatchItem(
        string watchItemTag, [FromQuery] string? eventType = null)
    {
        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, watchItemTag, StringComparison.OrdinalIgnoreCase));

        if (watchItem == null)
            return NotFound(new { error = $"WatchItem '{watchItemTag}' not found" });

        if (watchItem.Events.Count == 0)
            return BadRequest(new { error = $"WatchItem '{watchItemTag}' has no events" });

        // Select the requested event type, or default to the first event
        var evt = eventType is not null
            ? watchItem.Events.FirstOrDefault(e =>
                string.Equals(e.Type, eventType, StringComparison.OrdinalIgnoreCase))
            : watchItem.Events[0];

        if (evt == null)
            return BadRequest(new { error = $"Event type '{eventType}' not found on WatchItem '{watchItemTag}'" });

        // Atomic check-and-mark inside a lock to prevent TOCTOU race
        lock (_triggerLock)
        {
            if (_sessionManager.HasActiveExecution(watchItemTag))
                return Conflict(new { error = $"WatchItem '{watchItemTag}' is already running" });
        }

        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = watchItem.Path,
            SessionId = Guid.NewGuid().ToString("N")[..12],
            StartedUtc = DateTime.UtcNow,
        };

        // Fire and forget — ExecuteEventTrackedAsync handles session lifecycle
        _ = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteEventTrackedAsync(watchItemTag, evt, ctx, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await _hub.Clients.Group("global").SendAsync("ExecutionCancelled", new
                {
                    watchItemTag,
                    timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
            catch (Exception ex)
            {
                await _hub.Clients.Group("global").SendAsync("ExecutionError", new
                {
                    watchItemTag,
                    error = ex.Message,
                    timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
        });

        await _hub.Clients.Group("global").SendAsync("ExecutionStarted", new
        {
            watchItemTag,
            eventType = evt.Type,
            startTime = DateTime.UtcNow.ToString("o"),
            source = "WebClient",
        });

        return Accepted(new
        {
            message = $"WatchItem '{watchItemTag}' triggered (event: {evt.Type})",
        });
    }

    /// <summary>POST /api/execution/{sessionId}/cancel — cancel a running session.</summary>
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

    /// <summary>Safe DTO projection — strips internal state, paths, and sensitive fields.</summary>
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
