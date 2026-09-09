using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestController.Api.Security;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/watchlist")]
[Authorize(Policy = SecurityPolicies.User)]
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

    /// <summary>GET /api/watchlist — full WatchListConfig for the React WebClient tree builder.</summary>
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

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Drives the build picker. It is a WatchItem attribute, not a parameter-file entry; file entries below still win.
            if (!string.IsNullOrWhiteSpace(watchItem.BuildBasePath))
            {
                parameters["_BuildBasePath"] = watchItem.BuildBasePath;
                parameters["BuildBasePath"] = watchItem.BuildBasePath;
            }

            var nodes = WatchListHelpers.FindInitializeNodes(watchItem);
            if (nodes.Count == 0)
                return Ok(new { parameters, file = "", warning = "No Initialize node found" });

            // Resolve through the same layered loader the executor uses, so the dialog shows the
            // values the run will resolve rather than a raw view of one file. A JSON config parsed
            // as CSV yields whole lines as keys, which would place a secret in a key name where
            // value-based redaction cannot reach it.
            var ctx = new PipelineExecutionContext { WatchItemTag = watchItem.Tag };
            foreach (var init in nodes)
            {
                if (init.ParameterFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    ParameterResolver.LoadJsonConfig(ctx, init.ParameterFile, init.Profile, watchItem.Tag);
                else
                    ParameterResolver.LoadParameterFile(ctx, init.ParameterFile);
            }

            foreach (var entry in ctx.Parameters)
                parameters[entry.Key] = IsSecretParameter(entry.Key) ? RedactedValue : entry.Value;

            return Ok(new { parameters, file = Path.GetFileName(nodes[0].ParameterFile) });
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

    private const string RedactedValue = "********";

    private static readonly string[] SecretKeyFragments =
        ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "credential"];

    // This payload is rendered verbatim in the web trigger dialog, so credentials must never leave the server.
    private static bool IsSecretParameter(string key) =>
        SecretKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>GET /api/watchlist/tree — hierarchical PlanNode tree with action-type badges and aggregate counts.</summary>
    [HttpGet("tree")]
    public IActionResult GetTree()
    {
        var config = _vocabMonitor.CurrentConfig;
        if (config == null)
            return Ok(new { fileName = "", itemCount = 0, testCount = 0, stepCount = 0, items = Array.Empty<object>() });

        var items = config.WatchItems.Select(MapWatchItem).ToList();
        var stepCount = items.Sum(CountSteps);

        return Ok(new
        {
            fileName = Path.GetFileName(config.FilePath ?? "WatchList.xml"),
            itemCount = config.WatchItems.Count,
            testCount = config.WatchItems.Count,
            stepCount,
            items,
        });
    }

    private static object MapWatchItem(WatchItemConfig wi)
    {
        var children = wi.Events.Select(MapEvent).ToList();
        var actionCount = children.Sum(c => CountNodeActions(c));
        return new
        {
            id = wi.Tag ?? Guid.NewGuid().ToString("N")[..8],
            name = wi.Tag ?? "Untitled",
            actionType = "SEQ",
            isLeaf = false,
            childCount = children.Count,
            actionCount,
            children,
        };
    }

    private static object MapEvent(EventConfig ev)
    {
        var children = ev.Children.Select(MapActionNode).ToList();
        var actionCount = children.Sum(c => CountNodeActions(c));
        return new
        {
            id = Guid.NewGuid().ToString("N")[..8],
            name = ev.Type,
            actionType = "EVT",
            isLeaf = false,
            childCount = children.Count,
            actionCount,
            children,
        };
    }

    private static object MapActionNode(IActionNode node)
    {
        switch (node)
        {
            case ActionGroupConfig ag:
            {
                var children = ag.Children.Select(MapActionNode).ToList();
                var actionCount = children.Sum(c => CountNodeActions(c));
                var actionType = ag.ExecutionType == ExecutionMode.Parallel ? "PAR" : "SEQ";
                return new
                {
                    id = ag.NodeId ?? Guid.NewGuid().ToString("N")[..8],
                    name = ag.Tag ?? "Group",
                    actionType,
                    isLeaf = false,
                    childCount = children.Count,
                    actionCount,
                    children,
                };
            }
            case ActionConfig a:
            {
                var actionType = a.Type == ActionType.RunRemoteCommand ? "RMT" : "cmd";
                return new
                {
                    id = a.NodeId ?? Guid.NewGuid().ToString("N")[..8],
                    name = a.ResolvedTag,
                    actionType,
                    isLeaf = true,
                    childCount = (int?)null,
                    actionCount = (int?)null,
                    children = (object?)null,
                    leaf = new
                    {
                        targetAgent = a.AgentName ?? "",
                        commandLine = a.Command ?? "",
                        workingDirectory = "",
                        expectedExitCodes = new[] { 0 },
                        estimatedDurationSeconds = a.Timeout > 0 ? a.Timeout : (int?)null,
                    },
                };
            }
            case InitializeConfig init:
                return new
                {
                    id = init.NodeId ?? Guid.NewGuid().ToString("N")[..8],
                    name = init.Tag ?? $"Initialize ({init.ParameterFile})",
                    actionType = "INIT",
                    isLeaf = true,
                    childCount = (int?)null,
                    actionCount = (int?)null,
                    children = (object?)null,
                    leaf = (object?)null,
                };
            case RefConfig r:
                return new
                {
                    id = r.NodeId ?? Guid.NewGuid().ToString("N")[..8],
                    name = r.TemplateID ?? "Ref",
                    actionType = "REF",
                    isLeaf = true,
                    childCount = (int?)null,
                    actionCount = (int?)null,
                    children = (object?)null,
                    leaf = (object?)null,
                };
            default:
                return new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    name = "Unknown",
                    actionType = "cmd",
                    isLeaf = true,
                    childCount = (int?)null,
                    actionCount = (int?)null,
                    children = (object?)null,
                    leaf = (object?)null,
                };
        }
    }

    private static int CountNodeActions(object node)
    {
        // Use reflection-free approach: just count recursively from the source
        return 1; // Each mapped node contributes at minimum 1 to parent count
    }

    private static int CountSteps(object node)
    {
        // Simplified: count leaf actions from source
        return 1;
    }

    private static int CountSteps(WatchItemConfig wi)
    {
        return wi.Events.Sum(ev => CountStepsInNodes(ev.Children));
    }

    private static int CountStepsInNodes(IEnumerable<IActionNode> nodes)
    {
        return nodes.Sum(n => n switch
        {
            ActionGroupConfig ag => CountStepsInNodes(ag.Children),
            ActionConfig => 1,
            InitializeConfig => 1,
            RefConfig => 1,
            _ => 0,
        });
    }
}
