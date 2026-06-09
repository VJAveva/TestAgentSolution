using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TestController.WebApi.Services;

/// <summary>
/// Health check that verifies at least one registered agent is reachable via gRPC.
/// Reports Degraded when some agents are unreachable, Unhealthy when none are.
/// </summary>
public sealed class AgentConnectivityHealthCheck : IHealthCheck
{
    private readonly AgentRegistry _registry;
    private readonly AgentGrpcClientManager _clientManager;

    public AgentConnectivityHealthCheck(AgentRegistry registry, AgentGrpcClientManager clientManager)
    {
        _registry = registry;
        _clientManager = clientManager;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var agents = _registry.GetAll();
        if (agents.Count == 0)
            return HealthCheckResult.Healthy("No agents registered.");

        int reachable = 0;
        int unreachable = 0;
        var details = new Dictionary<string, object>();

        foreach (var agent in agents)
        {
            try
            {
                var client = _clientManager.GetClient(agent.Address);
                if (client is not null)
                {
                    reachable++;
                    details[agent.Name] = "reachable";
                }
                else
                {
                    unreachable++;
                    details[agent.Name] = "no client";
                }
            }
            catch (Exception ex)
            {
                unreachable++;
                details[agent.Name] = $"error: {ex.Message}";
            }
        }

        if (reachable == 0)
            return HealthCheckResult.Unhealthy($"All {unreachable} agents unreachable.", data: details);

        if (unreachable > 0)
            return HealthCheckResult.Degraded($"{reachable} reachable, {unreachable} unreachable.", data: details);

        return HealthCheckResult.Healthy($"All {reachable} agents reachable.", data: details);
    }
}
