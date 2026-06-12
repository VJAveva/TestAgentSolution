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

        // Phase 3b: WPF lock state service (subscribes to SignalR lock events)
        services.AddSingleton<LockStateService>();

        return services;
    }
}
