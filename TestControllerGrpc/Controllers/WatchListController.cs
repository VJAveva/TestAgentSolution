using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Controllers;

[ApiController]
[Route("api/watchlist")]
public class WatchListController : ControllerBase
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly ExecutionSessionManager _sessionManager;

    public WatchListController(
        IVocabularyMonitor vocabMonitor,
        ExecutionSessionManager sessionManager)
    {
        _vocabMonitor = vocabMonitor;
        _sessionManager = sessionManager;
    }

    /// <summary>GET /api/watchlist — WatchList tree as safe DTO (paths stripped).</summary>
    [HttpGet]
    public IActionResult GetWatchList()
    {
        var config = _vocabMonitor.CurrentConfig;
        if (config == null)
            return Ok(new { watchItems = Array.Empty<object>() });

        return Ok(new
        {
            watchItems = config.WatchItems.Select(wi => new
            {
                tag = wi.Tag,
                filter = wi.Filter,
                isEnabled = wi.IsEnabled,
                events = wi.Events.Select(e => new
                {
                    type = e.Type,
                    executionType = e.ExecutionType.ToString(),
                }),
            }),
        });
    }

    /// <summary>GET /api/watchlist/{tag}/status — execution status of a WatchItem.</summary>
    [HttpGet("{tag}/status")]
    public IActionResult GetStatus(string tag)
    {
        var isRunning = _sessionManager.HasActiveExecution(tag);
        var lastSession = _sessionManager.GetLastSession(tag);
        return Ok(new
        {
            tag,
            isRunning,
            lastSession = lastSession != null ? new
            {
                sessionId = lastSession.SessionId,
                state = lastSession.State.ToString(),
                startedUtc = lastSession.StartedUtc,
                completedUtc = lastSession.CompletedUtc,
                passed = lastSession.SucceededCount,
                failed = lastSession.FailedCount,
                total = lastSession.TotalActions,
            } : null
        });
    }
}
