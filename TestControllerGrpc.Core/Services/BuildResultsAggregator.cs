using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

public class BuildResultsAggregator
{
    private readonly BuildResultsConfig _config;

    public BuildResultsAggregator(BuildResultsConfig config) => _config = config;

    /// <summary>Evaluate the health status of a BuildNode and return an updated copy.</summary>
    public BuildNode EvaluateBuildHealth(BuildNode node)
    {
        var health = EvaluateHealth(node.PassRate);
        return node with { Health = health };
    }

    public HealthStatus EvaluateHealth(double passRate)
    {
        return passRate > _config.GoodThreshold ? HealthStatus.Good
            : passRate >= _config.WarningThreshold ? HealthStatus.Warning
            : HealthStatus.Bad;
    }
}
