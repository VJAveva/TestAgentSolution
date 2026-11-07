using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Features;

public static class FeatureGateServiceCollectionExtensions
{
    public const string ConfigPathKey = "Features:ConfigPath";

    /// <summary>Default when Features:ConfigPath is not set: Config\Features.json beside the host.</summary>
    public static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, "Config", "Features.json");

    public static IServiceCollection AddFeatureGate(this IServiceCollection services)
    {
        services.TryAddSingleton<IFeatureGate>(sp =>
        {
            var configured = sp.GetService<IConfiguration>()?[ConfigPathKey];
            var path = string.IsNullOrWhiteSpace(configured) ? DefaultConfigPath : configured;
            return new FeatureGate(path, sp.GetService<IAppLogger>());
        });
        // TryAdd: the standalone WebApi registers a forwarding implementation first.
        services.TryAddSingleton<IFeatureSettingsService>(sp => new FeatureSettingsService(
            sp.GetRequiredService<IFeatureGate>(),
            sp.GetService<TestControllerGrpc.Authorization.IAuditWriter>(),
            sp.GetService<IAppLogger>()));
        return services;
    }

    /// <summary>Registers a hosted service that only starts when <paramref name="featureKey"/> is on.</summary>
    public static IServiceCollection AddFeatureGatedHostedService<T>(this IServiceCollection services, string featureKey)
        where T : class, IHostedService
    {
        services.AddSingleton<IHostedService>(sp => new FeatureGatedHostedService<T>(sp, featureKey));
        return services;
    }
}

/// <summary>
/// Starts <typeparamref name="T"/> only if its feature is on at startup. The inner service is not even
/// constructed when off, so a disabled feature opens no files, timers or connections.
/// No registered gate (tests, older hosts) means on.
/// </summary>
public sealed class FeatureGatedHostedService<T>(IServiceProvider services, string featureKey) : IHostedService
    where T : class, IHostedService
{
    private T? _inner;

    public string FeatureKey => featureKey;
    internal T? Inner => _inner;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IFeatureGate>() is { } gate && !gate.IsEnabled(featureKey))
        {
            services.GetService<IAppLogger>()?.Info("Features",
                $"{typeof(T).Name} not started: feature '{featureKey}' is off on this controller.");
            return Task.CompletedTask;
        }

        _inner = ActivatorUtilities.GetServiceOrCreateInstance<T>(services);
        return _inner.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
