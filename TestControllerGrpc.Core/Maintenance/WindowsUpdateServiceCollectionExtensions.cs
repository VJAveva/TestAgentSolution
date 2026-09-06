using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Registers the Windows Update posture pipeline: policy store, status store, evaluator, notification feed and the
/// coordinator that turns posture into a <see cref="MaintenanceState"/>. Both hosts call this so the dispatch gate
/// behaves identically whichever one is scheduling (spec §14–§16).
/// </summary>
public static class WindowsUpdateServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsUpdatePosture(
        this IServiceCollection services, UpdatePolicy? initialPolicy = null)
    {
        services.TryAddSingleton<IUpdatePolicyStore>(_ => new UpdatePolicyStore(initialPolicy ?? new UpdatePolicy()));
        services.TryAddSingleton<INodeUpdateStatusStore, NodeUpdateStatusStore>();
        services.TryAddSingleton<IUpdatePolicyEvaluator, UpdatePolicyEvaluator>();
        services.TryAddSingleton<IFleetNotificationService, FleetNotificationService>();
        // Already registered by AddFleetMaintenanceServices in the WPF host; the WebApi host needs its own.
        services.TryAddSingleton<IMaintenanceStateStore, MaintenanceStateStore>();
        services.TryAddSingleton<UpdatePostureCoordinator>();
        services.AddHostedService<UpdatePostureHostedService>();
        return services;
    }
}
