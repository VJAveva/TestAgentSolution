using Microsoft.AspNetCore.Mvc;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly IAgentGrpcDispatcher _dispatcher;

    public AgentsController(IAgentGrpcDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>GET /api/agents � all registered agents with health status.</summary>
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
}
