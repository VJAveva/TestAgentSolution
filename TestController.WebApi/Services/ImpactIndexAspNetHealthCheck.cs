using Microsoft.Extensions.Diagnostics.HealthChecks;
using TestControllerGrpc.Core.Impact;

namespace TestController.WebApi.Services;

/// <summary>
/// Surfaces <see cref="IImpactIndexHealthCheck"/> at <c>/health/impact-index</c>. Reported as Unhealthy when
/// the index cannot serve queries and Degraded when it is merely stale, so a monitor distinguishes "wrong
/// answers" from "possibly incomplete answers".
/// </summary>
public sealed class ImpactIndexAspNetHealthCheck : IHealthCheck
{
    private readonly IImpactIndexHealthCheck _inner;

    public ImpactIndexAspNetHealthCheck(IImpactIndexHealthCheck inner) => _inner = inner;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ImpactIndexHealth health = await _inner.CheckAsync(cancellationToken).ConfigureAwait(false);

        var data = new Dictionary<string, object>
        {
            ["status"] = health.Status.ToString(),
            ["indexFilePath"] = health.IndexFilePath,
            ["sizeBytes"] = health.SizeBytes,
            ["documentCount"] = health.DocumentCount,
            ["lastBuiltUtc"] = health.LastBuiltUtc?.ToString("O") ?? "",
        };

        return health.Status switch
        {
            ImpactIndexStatus.Ready => HealthCheckResult.Healthy(health.Message, data),
            ImpactIndexStatus.Stale => HealthCheckResult.Degraded(health.Message, data: data),
            _ => HealthCheckResult.Unhealthy(health.Message, data: data),
        };
    }
}
