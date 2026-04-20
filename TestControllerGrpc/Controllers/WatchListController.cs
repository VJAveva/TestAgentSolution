using System.IO;
using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Models;
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

    /// <summary>GET /api/watchlist — full WatchList tree with execution status.</summary>
    [HttpGet]
    public IActionResult GetWatchList()
    {
        var config = _vocabMonitor.CurrentConfig;
        if (config == null)
            return Ok(new { watchItems = Array.Empty<object>() });

        return Ok(new
        {
            watchItems = config.WatchItems.Select(wi =>
            {
                var isRunning = _sessionManager.HasActiveExecution(wi.Tag);
                var lastSession = _sessionManager.GetLastSession(wi.Tag);
                var executionStatus = isRunning ? "Running"
                    : lastSession?.State == SessionState.Completed ? "Success"
                    : lastSession?.State == SessionState.Failed ? "Failed"
                    : lastSession?.State == SessionState.PartialFailure ? "Failed"
                    : "Idle";

                return new
                {
                    tag = wi.Tag,
                    nodeKind = "WatchItem",
                    filter = wi.Filter,
                    isEnabled = wi.IsEnabled,
                    buildBasePath = wi.BuildBasePath,
                    executionStatus,
                    children = wi.Events.Select(e => SerializeEvent(e)).ToList(),
                };
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

    /// <summary>GET /api/watchlist/{tag}/parameters — current Variables.txt values.</summary>
    [HttpGet("{tag}/parameters")]
    public IActionResult GetParameters(string tag)
    {
        var config = _vocabMonitor.CurrentConfig;
        var watchItem = config?.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));

        if (watchItem == null)
            return NotFound(new { error = $"WatchItem '{tag}' not found" });

        var paramFile = FindInitializeFile(watchItem);
        if (paramFile == null || !System.IO.File.Exists(paramFile))
            return Ok(new { parameters = new Dictionary<string, string>(), file = "" });

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

    // ?? Tree serialization helpers ???????????????????????????????????????

    private static object SerializeEvent(EventConfig e) => new
    {
        tag = e.Type,
        nodeKind = "Event",
        executionType = e.ExecutionType.ToString(),
        executionStatus = "Idle",
        children = e.Children.Select(SerializeNode).ToList(),
    };

    private static object SerializeNode(IActionNode node) => node switch
    {
        ActionGroupConfig g => new
        {
            tag = g.Tag,
            nodeKind = "ActionGroup",
            executionType = g.ExecutionType.ToString(),
            executionStatus = "Idle",
            agentName = (string?)null,
            command = (string?)null,
            children = g.Children.Select(SerializeNode).ToList(),
        },
        ActionConfig a => (object)new
        {
            tag = !string.IsNullOrEmpty(a.Order) ? a.Order : a.Command,
            nodeKind = "Action",
            executionStatus = "Idle",
            agentName = string.IsNullOrEmpty(a.AgentName) ? "Controller" : a.AgentName,
            command = a.Command,
            children = Array.Empty<object>(),
        },
        InitializeConfig i => (object)new
        {
            tag = i.Tag,
            nodeKind = "Initialize",
            executionStatus = "Idle",
            agentName = (string?)null,
            command = (string?)null,
            children = Array.Empty<object>(),
        },
        RefConfig r => (object)new
        {
            tag = r.TemplateID,
            nodeKind = "Ref",
            executionStatus = "Idle",
            agentName = (string?)null,
            command = (string?)null,
            children = Array.Empty<object>(),
        },
        _ => new
        {
            tag = "Unknown",
            nodeKind = "Unknown",
            executionStatus = "Idle",
            agentName = (string?)null,
            command = (string?)null,
            children = Array.Empty<object>(),
        },
    };

    private static string? FindInitializeFile(WatchItemConfig watchItem)
    {
        foreach (var ev in watchItem.Events)
        {
            var file = FindInitializeFileInChildren(ev.Children);
            if (file != null) return file;
        }
        return null;
    }

    private static string? FindInitializeFileInChildren(List<IActionNode> children)
    {
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
                return init.ParameterFile;
            if (child is ActionGroupConfig group)
            {
                var file = FindInitializeFileInChildren(group.Children);
                if (file != null) return file;
            }
        }
        return null;
    }
}
