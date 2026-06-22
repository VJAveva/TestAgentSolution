using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestController.Api.Security;
using TestController.Api.Services;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/agents")]
[Authorize(Policy = SecurityPolicies.User)]
public class AgentsController : ControllerBase
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;

    public AgentsController(IAgentGrpcDispatcher dispatcher, AgentLockManager lockManager)
    {
        _dispatcher = dispatcher;
        _lockManager = lockManager;
    }

    /// <summary>GET /api/agents — all registered agents with health status.</summary>
    [HttpGet]
    public IActionResult GetAgents()
    {
        var agents = _dispatcher.RegisteredAgents.Select(name =>
        {
            var health = _dispatcher.GetAgentHealth(name);
            return new
            {
                name,
                address = _dispatcher.GetAgentAddress(name),
                status = health is null ? "Unknown"
                       : health.IsHealthy ? "Healthy"
                       : health.CircuitOpenedUtc.HasValue ? "CircuitOpen"
                       : "Unhealthy",
                lastCheckedUtc = health?.LastSuccessUtc?.ToString("o"),
                health,
            };
        });
        return Ok(agents);
    }

    /// <summary>
    /// GET /api/agents/fleet — every registered agent plus its agent-lock / running-pipeline
    /// state, in the shape the WebClient fleet view expects. Served by BOTH hosts from the
    /// same code path so the React fleet and the WPF fleet agree.
    ///
    /// When this runs in the standalone WebApi co-located with a WPF controller, the controller
    /// is the authoritative owner of agent locks (web runs are forwarded to it), so the snapshot
    /// is proxied to the controller's identical endpoint via <see cref="IControllerFleetProxy"/>.
    /// Standalone (no proxy) and the controller host itself build the snapshot locally.
    /// </summary>
    [HttpGet("fleet")]
    public async Task<IActionResult> GetFleet()
    {
        var proxy = HttpContext.RequestServices.GetService(typeof(IControllerFleetProxy)) as IControllerFleetProxy;
        if (proxy is { IsConfigured: true })
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            var json = await proxy.GetFleetJsonAsync(authHeader, HttpContext.RequestAborted);
            if (json is not null)
                return Content(json, "application/json");
            // Controller unreachable — fall through to a local snapshot rather than erroring.
        }

        var locks = _lockManager.GetAllLocks()
            .ToDictionary(l => l.AgentName, StringComparer.OrdinalIgnoreCase);

        var fleet = _dispatcher.RegisteredAgents.Select(name =>
        {
            var health = _dispatcher.GetAgentHealth(name);
            locks.TryGetValue(name, out var agentLock);
            return new
            {
                name,
                address = _dispatcher.GetAgentAddress(name),
                status = health is null ? "Unknown"
                       : health.IsHealthy ? "Online"
                       : health.CircuitOpenedUtc.HasValue ? "Unreachable"
                       : "Offline",
                lastStatusDetail = health is { IsHealthy: false, ConsecutiveFailures: > 0 }
                    ? $"{health.ConsecutiveFailures} consecutive failure(s)"
                    : null,
                lastCheckedUtc = health?.LastSuccessUtc?.ToString("o"),
                isLocked = agentLock is not null,
                lockedBy = agentLock?.SessionId,
                lockSource = agentLock?.Source,
                lockedAtUtc = agentLock?.LockedAtUtc.ToString("o"),
                watchItemTag = agentLock?.WatchItemTag,
            };
        });

        return Ok(new { agents = fleet, lockVersion = _lockManager.Version });
    }
}

