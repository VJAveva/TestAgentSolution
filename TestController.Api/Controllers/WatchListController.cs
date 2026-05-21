using System.IO;
using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

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

    /// <summary>GET /api/watchlist � full WatchListConfig for the React WebClient tree builder.</summary>
    [HttpGet]
    public IActionResult GetWatchList()
    {
        try
        {
            var config = _vocabMonitor.CurrentConfig;
            if (config == null || config.WatchItems.Count == 0)
            {
                return Ok(new
                {
                    watchItems = Array.Empty<object>(),
                    templates = Array.Empty<object>(),
                    filePath = config?.FilePath ?? "",
                    warning = config == null
                        ? "WatchList is not loaded. Check if WatchList.xml path is configured."
                        : "WatchList is empty � no WatchItems defined.",
                });
            }

            return Ok(config);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = "Failed to load WatchList",
                detail = ex.Message,
                configPath = _vocabMonitor.CurrentConfig?.FilePath ?? "(not set)",
            });
        }
    }

    /// <summary>GET /api/watchlist/{tag}/status � execution status of a WatchItem.</summary>
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

    /// <summary>GET /api/watchlist/{tag}/parameters � current Variables.txt values.</summary>
    [HttpGet("{tag}/parameters")]
    public IActionResult GetParameters(string tag)
    {
        try
        {
            var config = _vocabMonitor.CurrentConfig;
            var watchItem = config?.WatchItems
                .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));

            if (watchItem == null)
                return NotFound(ApiErrorFactory.InvalidTag(tag));

            var paramFile = WatchListHelpers.FindInitializeFile(watchItem);
            if (paramFile == null || !System.IO.File.Exists(paramFile))
                return Ok(new { parameters = new Dictionary<string, string>(), file = "", warning = paramFile == null ? "No Initialize node found" : $"Parameter file not found: {Path.GetFileName(paramFile)}" });

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var entries = ParameterResolver.ParseParameterFile(paramFile);
            foreach (var (key, value) in entries)
            {
                parameters[key] = value;
                if (key.StartsWith('_'))
                    parameters[key[1..]] = value;
            }

            return Ok(new { parameters, file = Path.GetFileName(paramFile) });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = $"Failed to load parameters for '{tag}'",
                detail = ex.Message,
            });
        }
    }
}
