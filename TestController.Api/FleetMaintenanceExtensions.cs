using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Persistence.Maintenance;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.Api;

/// <summary>
/// Groups the fleet-maintenance (revert) DI registrations, following the AddRbacFeature() / AddControllerApi()
/// pattern. Only the maintenance-specific services are registered here; the engine's node / lock / session /
/// dispatcher dependencies are supplied by the host. Call from the primary host (WPF Controller), which owns the
/// SQLite database that <see cref="IMaintenanceOperationStore"/> writes to.
/// </summary>
public static class FleetMaintenanceExtensions
{
    public static IServiceCollection AddFleetMaintenanceServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ =>
            configuration.GetSection("Maintenance").Get<MaintenanceOptions>() ?? new MaintenanceOptions());

        services.AddSingleton<IPowerShellScriptRunner, PowerShellScriptRunner>();
        services.AddSingleton<INodeReadinessProbe, NodeReadinessProbe>();
        services.AddSingleton<IMaintenanceStateStore, MaintenanceStateStore>();
        services.AddSingleton<IMaintenanceOperationStore, MaintenanceOperationStore>();
        services.AddSingleton<IMachineRevertOperation, MachineRevertOperation>();
        services.AddSingleton<IMachineRebootOperation, MachineRebootOperation>();
        services.AddSingleton<IFleetMaintenanceService, FleetMaintenanceService>();

        return services;
    }
}
