using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// The builder engine was fully written and fully tested while being unreachable: nothing
/// registered it, no data files shipped, and every existing test hand-built the service with temp
/// folders. These guards cover the seams those tests cannot see - container resolution, the data
/// files that actually ship, and whether they reach a deployed controller at all.
/// </summary>
public class PipelineBuilderWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string RecipesRoot() => Path.Combine(RepoRoot(), "pipelines", "recipes");

    private static string TemplatesRoot() => Path.Combine(RepoRoot(), "pipelines", "templates");

    private static ServiceProvider BuildContainer()
    {
        var dispatcher = new Mock<IAgentGrpcDispatcher>();
        dispatcher.SetupGet(d => d.RegisteredAgents).Returns([]);
        dispatcher.Setup(d => d.GetAllAgentHealth())
            .Returns(new Dictionary<string, AgentHealthState>(StringComparer.OrdinalIgnoreCase));

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PipelineBuilderFeatureExtensions.RecipesRootKey] = RecipesRoot(),
                [PipelineBuilderFeatureExtensions.TemplatesRootKey] = TemplatesRoot(),
            })
            .Build());
        services.AddSingleton<IAppLogger, SilentLogger>();
        services.AddSingleton(new Mock<IAuthorizationService>().Object);
        services.AddSingleton(dispatcher.Object);
        services.AddSingleton(new Mock<IVocabularyMonitor>().Object);
        services.AddSingleton<CurrentUserHolder>();
        services.AddSingleton<CapabilityChecker>();
        services.AddPipelineBuilderFeature();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The gap this whole exercise existed to close: the view model was complete and tested, but
    /// nothing in the container could build one, so the panel could never have opened.
    /// </summary>
    [Fact]
    public void AddPipelineBuilderFeature_Should_ResolveTheViewModel_When_TheContainerIsBuilt()
    {
        using var provider = BuildContainer();

        var vm = provider.GetService<global::TestControllerGrpc.ViewModels.PipelineBuilder.PipelineBuilderViewModel>();

        Assert.NotNull(vm);
    }

    [Fact]
    public void AddPipelineBuilderFeature_Should_ResolveTheService_When_TheContainerIsBuilt()
    {
        using var provider = BuildContainer();

        Assert.NotNull(provider.GetService<PipelineBuilderService>());
        Assert.NotNull(provider.GetService<IBuilderAgentSource>());
    }

    /// <summary>A shipped recipe the loader rejects is a recipe nobody can pick.</summary>
    [Fact]
    public void ShippedRecipes_Should_Load_When_ReadFromTheRepoFolder()
    {
        var result = new FolderRecipeSource(RecipesRoot()).GetRecipes();

        Assert.Empty(result.Problems);
        Assert.NotEmpty(result.Recipes);
    }

    [Fact]
    public void ShippedTemplates_Should_Load_When_ReadFromTheRepoFolder()
    {
        var result = new FolderStageTemplateSource(TemplatesRoot()).GetTemplates();

        Assert.Empty(result.Problems);
        Assert.NotEmpty(result.Templates);
    }

    /// <summary>
    /// A stage naming a template that does not ship previews as an empty stage rather than failing
    /// loudly, so a plain typo would reach the author as a silently incomplete pipeline.
    /// </summary>
    [Fact]
    public void EveryShippedRecipeStage_Should_NameAShippedTemplate()
    {
        var recipes = new FolderRecipeSource(RecipesRoot()).GetRecipes().Recipes;
        var templateIds = new FolderStageTemplateSource(TemplatesRoot())
            .GetTemplates().Templates
            .Select(t => t.ID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = recipes
            .SelectMany(r => r.Stages.Select(s => (Recipe: r.Name, s.TemplateId)))
            .Where(x => !templateIds.Contains(x.TemplateId))
            .Select(x => $"{x.Recipe} -> {x.TemplateId}")
            .ToList();

        Assert.True(missing.Count == 0, "Recipe stages name templates that do not ship: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The FIR rule packs shipped without this entry once and reached the controller as a missing
    /// folder, which degraded the feature silently. Same data-beside-the-exe shape, same guard.
    /// </summary>
    [Theory]
    [InlineData("TestControllerGrpc", "TestControllerGrpc.csproj")]
    [InlineData("TestController.WebApi", "TestController.WebApi.csproj")]
    public void BothHosts_Should_PublishTheBuilderDataFiles(string project, string csproj)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), project, csproj));

        foreach (var folder in new[] { @"pipelines\recipes", @"pipelines\templates" })
        {
            var line = text.Split('\n').FirstOrDefault(l => l.Contains(folder, StringComparison.OrdinalIgnoreCase));
            Assert.True(line is not null, $"{csproj} does not ship {folder}.");
            Assert.Contains("CopyToPublishDirectory", line!, StringComparison.Ordinal);
        }
    }

    private sealed class SilentLogger : IAppLogger
    {
        public event Action<AppLogEntry>? EntryAdded;

        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? ex = null) { }

        public void Log(Microsoft.Extensions.Logging.LogLevel level, string category, string message,
            string? correlationId, long elapsedMs = 0, Exception? ex = null) { }

        public void LogStructured(Microsoft.Extensions.Logging.LogLevel level, string category, string message,
            string? agent = null, string? runId = null, string? pipeline = null,
            string? action = null, long elapsedMs = 0, Exception? ex = null) { }

        public void Info(string category, string message) { }

        public void Warn(string category, string message) { }

        public void Error(string category, string message, Exception? ex = null) { }

        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
