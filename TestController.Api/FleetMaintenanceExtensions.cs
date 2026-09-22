using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Persistence.Maintenance;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

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
        services.AddSingleton(sp => BuildVirtualizationProvider(sp, configuration));

        // Inert until something resolves it: IGoldenImageRefreshOperation is deliberately NOT registered.
        services.AddSingleton<INodeUpdateInstaller, NodeUpdateInstaller>();

        return services;
    }

    /// <summary>
    /// Snapshot/power operations for golden-image work. A factory rather than a plain AddSingleton because the
    /// provider needs options and platform capabilities that DI cannot resolve on its own.
    /// </summary>
    private static IVirtualizationProvider BuildVirtualizationProvider(IServiceProvider sp, IConfiguration configuration)
    {
        var section = configuration.GetSection("Maintenance:Virtualization");
        var platformId = (section["PlatformId"] ?? "vcloud").Trim().ToLowerInvariant();

        var capabilities = platformId switch
        {
            "vcloud" => VmPlatformCapabilities.VCloud,
            "vsphere" => VmPlatformCapabilities.VSphere,
            "hyperv" => VmPlatformCapabilities.HyperV,
            _ => throw new InvalidOperationException(
                $"Maintenance:Virtualization:PlatformId '{platformId}' is not supported. Use vcloud, vsphere or hyperv."),
        };

        var scriptPath = section["ScriptPath"];
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            // Vm-Ops.<platform>.ps1 ships beside Revert-AgentVM.ps1, so deriving it keeps the hand-tuned
            // deployed appsettings (which is ahead of source) from needing an edit.
            var revertDir = Path.GetDirectoryName(sp.GetRequiredService<MaintenanceOptions>().RevertScriptPath);
            if (!string.IsNullOrWhiteSpace(revertDir))
                scriptPath = Path.Combine(revertDir, $"Vm-Ops.{platformId}.ps1");
        }

        if (string.IsNullOrWhiteSpace(scriptPath))
            throw new InvalidOperationException(
                "Maintenance:Virtualization:ScriptPath is not set and could not be derived, because "
                + "Maintenance:RevertScriptPath is empty. Set one of them.");

        return new ScriptBackedVirtualizationProvider(
            sp.GetRequiredService<IPowerShellScriptRunner>(),
            new VirtualizationProviderOptions { PlatformId = platformId, ScriptPath = scriptPath },
            capabilities,
            sp.GetRequiredService<IAppLogger>());
    }
}
