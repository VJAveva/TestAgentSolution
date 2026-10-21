using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TestControllerGrpc.Core.Fir;
using TestControllerGrpc.Reporting.Fir;
using TestControllerGrpc.Services;

namespace TestController.Api;

/// <summary>
/// Groups the FIR registrations, following AddRbacFeature() / AddFleetMaintenanceServices().
/// Called from AddControllerApi(), which both API hosts use, so the two containers cannot drift.
/// </summary>
public static class FirFeatureExtensions
{
    public const string RulesPathKey = "Fir:RulesPath";
    public const string TestDataRootsKey = "Fir:TestDataRoots";

    public static IServiceCollection AddFirFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IConfiguration>()[RulesPathKey];
            var folder = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "rules")
                : configured;

            var loaded = FirRulePackLoader.LoadDirectory(folder);
            var logger = sp.GetRequiredService<IAppLogger>();

            // A malformed pack is one team's problem; say so loudly but keep the others.
            foreach (var error in loaded.Errors)
                logger.Warn("Fir", $"rule pack rejected - {error}");

            logger.Info("Fir", $"Loaded {loaded.Packs.Count} rule pack(s) from {folder}");
            return new FirRulePackSet(loaded.Packs, loaded.Errors);
        });

        services.TryAddSingleton(sp => new FirOptions
        {
            TestDataRoots = sp.GetRequiredService<IConfiguration>()
                .GetSection(TestDataRootsKey).Get<string[]>() ?? [],
        });

        services.TryAddSingleton<ITestDataSheetReader, ClosedXmlTestDataSheetReader>();

        services.TryAddSingleton(sp => new FirAnalyzer(
            sp.GetRequiredService<FirRulePackSet>().Packs,
            sp.GetRequiredService<ITestDataSheetReader>(),
            sp.GetRequiredService<FirOptions>()));

        services.TryAddSingleton<FirService>();
        return services;
    }
}
