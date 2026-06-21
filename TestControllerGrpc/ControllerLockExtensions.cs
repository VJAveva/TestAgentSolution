using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Hubs;
using TestControllerGrpc.Locking;
using TestControllerGrpc.Services;

namespace TestControllerGrpc;

/// <summary>
/// Controller-host-only lock registrations. NOT in AddRbacFeature() — the standalone
/// WebApi never instantiates LockRegistry; it forwards via ControllerProxyService.
/// Per phase-3a-context.md "Watch out for" #1.
/// </summary>
public static class ControllerLockExtensions
{
    public static IServiceCollection AddControllerLockServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LockOptions>(configuration.GetSection(LockOptions.SectionName));
        services.AddSingleton<LockRegistry>();
        services.AddSingleton<ILockRegistry>(sp => sp.GetRequiredService<LockRegistry>());
        services.AddHostedService<LockExpirySweeper>();
        services.AddSingleton<LockBroadcaster>();
        // Activate the broadcaster: it subscribes to ILockRegistry.OnLockEvent in StartAsync.
        // Without this hosted-service registration the singleton is never resolved and the
        // PipelineLock* SignalR events are never broadcast.
        services.AddHostedService(sp => sp.GetRequiredService<LockBroadcaster>());

        // Phase 3b: WPF lock state service (subscribes to SignalR lock events)
        services.AddSingleton<LockStateService>();
        // Activate it: StartAsync subscribes to ILockRegistry.OnLockEvent in-process so the
        // WPF tree/dashboard/fleet reflect locks for in-process WPF-origin runs.
        services.AddHostedService(sp => sp.GetRequiredService<LockStateService>());

        return services;
    }
}
