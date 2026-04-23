using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly IAgentGrpcDispatcher _dispatcher;

    public HealthController(
        IVocabularyMonitor vocabMonitor,
        ExecutionSessionManager sessionManager,
        IAgentGrpcDispatcher dispatcher)
    {
        _vocabMonitor = vocabMonitor;
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
    }

    /// <summary>GET /api/health — lightweight liveness check.</summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        var config = _vocabMonitor.CurrentConfig;
        return Ok(new
        {
            status = "ok",
            timestamp = DateTime.UtcNow,
            server = Environment.MachineName,
            components = new
            {
                watchList = new { loaded = config != null, watchItems = config?.WatchItems.Count ?? 0 },
                agents = new { registered = _dispatcher.RegisteredAgentCount },
                execution = new { activeSessions = _sessionManager.ActiveExecutionCount },
                signalR = new { status = "available" },
            },
        });
    }

    /// <summary>GET /api/health/diagnostics — detailed system info for debugging.</summary>
    [HttpGet("health/diagnostics")]
    public IActionResult Diagnostics()
    {
        var config = _vocabMonitor.CurrentConfig;
        var process = Process.GetCurrentProcess();

        return Ok(new
        {
            watchList = new
            {
                filePath = config?.FilePath ?? "(not loaded)",
                watchItemCount = config?.WatchItems.Count ?? 0,
                templateCount = config?.Templates.Count ?? 0,
                watchItems = config?.WatchItems.Select(wi => new
                {
                    tag = wi.Tag,
                    isEnabled = wi.IsEnabled,
                    eventCount = wi.Events.Count,
                }) ?? [],
            },
            agents = _dispatcher.RegisteredAgents.Select(name => new
            {
                name,
                address = _dispatcher.GetAgentAddress(name),
                health = _dispatcher.GetAgentHealth(name) is { } h ? new
                {
                    isHealthy = h.IsHealthy,
                    consecutiveFailures = h.ConsecutiveFailures,
                    lastSuccessUtc = h.LastSuccessUtc,
                } : null,
            }),
            execution = new
            {
                activeSessions = _sessionManager.GetActiveSessions().Select(s => new
                {
                    sessionId = s.SessionId,
                    watchItemTag = s.WatchItemTag,
                    eventType = s.EventType,
                    state = s.State.ToString(),
                    startedUtc = s.StartedUtc,
                    actions = s.TotalActions,
                    passed = s.SucceededCount,
                    failed = s.FailedCount,
                }),
                recentHistory = _sessionManager.GetHistory(5).Select(s => new
                {
                    sessionId = s.SessionId,
                    watchItemTag = s.WatchItemTag,
                    state = s.State.ToString(),
                    completedUtc = s.CompletedUtc,
                    summary = s.SummaryText,
                }),
            },
            environment = new
            {
                machineName = Environment.MachineName,
                dotnetVersion = Environment.Version.ToString(),
                processId = Environment.ProcessId,
                workingDirectory = Environment.CurrentDirectory,
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"dd\.hh\:mm\:ss"),
            },
        });
    }
}
