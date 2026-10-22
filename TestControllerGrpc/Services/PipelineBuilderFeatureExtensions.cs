using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Registers the Pipeline Builder for the desktop container, following AddFirFeature().
/// </summary>
/// <remarks>
/// Desktop-only for now: the panel is the only consumer. When the web path gains endpoints this
/// moves to TestController.Api so both API hosts share one registration.
/// </remarks>
public static class PipelineBuilderFeatureExtensions
{
    public const string ParametersRootKey = "PipelineBuilder:ParametersRoot";
    public const string RecipesRootKey = "PipelineBuilder:RecipesRoot";
    public const string TemplatesRootKey = "PipelineBuilder:TemplatesRoot";
    public const string TriggerRootKey = "PipelineBuilder:TriggerRoot";
    public const string FragmentDirKey = "PipelineBuilder:FragmentDir";

    public static IServiceCollection AddPipelineBuilderFeature(this IServiceCollection services)
    {
        services.TryAddSingleton<IBuilderAgentSource, DispatcherBuilderAgentSource>();

        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetRequiredService<IAppLogger>();

            var recipesRoot = Resolve(config, RecipesRootKey, BesideExe("pipelines", "recipes"));
            var templatesRoot = Resolve(config, TemplatesRootKey, BesideExe("pipelines", "templates"));
            var parametersRoot = Resolve(config, ParametersRootKey, Path.Combine(ControllerRoot(config), "Parameters"));
            var triggerRoot = Resolve(config, TriggerRootKey, @"C:\Triggers");

            // A team that drops in a malformed recipe should learn about it at startup, not when the
            // panel silently shows one fewer row.
            var recipes = new FolderRecipeSource(recipesRoot);
            foreach (var problem in recipes.GetRecipes().Problems)
                logger.Warn("PipelineBuilder", $"recipe rejected - {problem}");

            var templates = new FolderStageTemplateSource(templatesRoot);
            foreach (var problem in templates.GetTemplates().Problems)
                logger.Warn("PipelineBuilder", $"stage template rejected - {problem}");

            logger.Info("PipelineBuilder", $"recipes={recipesRoot} templates={templatesRoot} parameters={parametersRoot}");

            return new PipelineBuilderService(
                new DerivedTargetCatalog(parametersRoot),
                recipes,
                templates,
                sp.GetRequiredService<IAuthorizationService>(),
                triggerRoot);
        });

        // Transient: each open re-reads the catalogs, so a config dropped in since the last open shows up.
        services.TryAddTransient(sp => new ViewModels.PipelineBuilder.PipelineBuilderViewModel(
            sp.GetRequiredService<PipelineBuilderService>(),
            sp.GetRequiredService<IBuilderAgentSource>(),
            sp.GetRequiredService<CapabilityChecker>(),
            sp.GetRequiredService<CurrentUserHolder>(),
            () => sp.GetRequiredService<IVocabularyMonitor>().CurrentConfig ?? new WatchListConfig(),
            Resolve(
                sp.GetRequiredService<IConfiguration>(),
                FragmentDirKey,
                Path.Combine(ControllerRoot(sp.GetRequiredService<IConfiguration>()), "PipelineFragments"))));

        return services;
    }

    private static string Resolve(IConfiguration config, string key, string fallback)
        => config[key] is { Length: > 0 } value ? value : fallback;

    private static string BesideExe(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, .. parts]);

    /// <summary>
    /// Derived from the WatchList location rather than configured separately - the controller keeps
    /// its configs beside it, and a second setting would be a second thing to get wrong.
    /// </summary>
    private static string ControllerRoot(IConfiguration config)
    {
        var vocabulary = config["VocabularyFile"];
        if (string.IsNullOrWhiteSpace(vocabulary)) return AppContext.BaseDirectory;

        return Path.GetDirectoryName(vocabulary) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;
    }
}
