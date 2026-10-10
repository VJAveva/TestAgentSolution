using System.Text.Json;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>How a stage's agent-generic template is attached to the chosen agents.</summary>
public enum AgentMapping
{
    /// <summary>One node, on the first chosen agent.</summary>
    Single,

    /// <summary>One Parallel group with a copy of the stage per chosen agent.</summary>
    PoolFanout,

    /// <summary>The first agent alone, then a Parallel group for the rest.</summary>
    OrderedFirstThenRest,
}

public sealed record RecipeStage(string Name, string TemplateId, AgentMapping AgentMapping);

/// <summary>A named, ordered list of stages. Teams add one by dropping a file - no rebuild.</summary>
public sealed record Recipe(string Name, IReadOnlyList<RecipeStage> Stages);

public sealed record RecipeCatalogResult(
    IReadOnlyList<Recipe> Recipes,
    IReadOnlyList<string> Problems);

public interface IRecipeSource
{
    RecipeCatalogResult GetRecipes();
}

/// <summary>Loads every <c>*.json</c> recipe in a folder teams own.</summary>
public sealed class FolderRecipeSource(string recipesRoot) : IRecipeSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Accepts the hyphenated spelling the recipe files use as well as the enum name, so a team can
    /// write <c>pool-fanout</c> without knowing the C# member.
    /// </summary>
    public static bool TryParseAgentMapping(string? raw, out AgentMapping mapping)
    {
        mapping = AgentMapping.Single;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalised = raw.Replace("-", "").Replace("_", "").Trim();
        return Enum.TryParse(normalised, ignoreCase: true, out mapping);
    }

    public RecipeCatalogResult GetRecipes()
    {
        var recipes = new List<Recipe>();
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(recipesRoot) || !Directory.Exists(recipesRoot))
            return new RecipeCatalogResult(recipes, problems);

        foreach (var path in Directory
            .EnumerateFiles(recipesRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            RecipeDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<RecipeDto>(File.ReadAllText(path), Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                problems.Add($"'{name}' could not be read as JSON: {ex.Message}");
                continue;
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            {
                problems.Add($"'{name}' has no recipe name.");
                continue;
            }

            if (dto.Stages is not { Count: > 0 })
            {
                problems.Add($"Recipe '{dto.Name}' has no stages.");
                continue;
            }

            var stages = new List<RecipeStage>();
            var bad = false;
            foreach (var stage in dto.Stages)
            {
                if (string.IsNullOrWhiteSpace(stage.Name) || string.IsNullOrWhiteSpace(stage.TemplateId))
                {
                    problems.Add($"Recipe '{dto.Name}' has a stage missing name or templateId.");
                    bad = true;
                    break;
                }

                if (!TryParseAgentMapping(stage.AgentMapping, out var mapping))
                {
                    problems.Add(
                        $"Recipe '{dto.Name}' stage '{stage.Name}' has unknown agentMapping "
                        + $"'{stage.AgentMapping}'. Known: single, pool-fanout, ordered-first-then-rest.");
                    bad = true;
                    break;
                }

                stages.Add(new RecipeStage(stage.Name, stage.TemplateId, mapping));
            }

            if (bad) continue;

            if (recipes.Any(r => string.Equals(r.Name, dto.Name, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"Recipe name '{dto.Name}' is defined more than once.");
                continue;
            }

            recipes.Add(new Recipe(dto.Name, stages));
        }

        return new RecipeCatalogResult(recipes, problems);
    }

    private sealed class RecipeDto
    {
        public string? Name { get; set; }
        public List<StageDto>? Stages { get; set; }
    }

    private sealed class StageDto
    {
        public string? Name { get; set; }
        public string? TemplateId { get; set; }
        public string? AgentMapping { get; set; }
    }
}

public sealed record StageTemplateResult(
    IReadOnlyList<TemplateConfig> Templates,
    IReadOnlyList<string> Problems);

public interface IStageTemplateSource
{
    StageTemplateResult GetTemplates();
}

/// <summary>
/// Loads agent-generic stage templates from a folder of WatchList <c>&lt;Templates&gt;</c> XML.
/// </summary>
/// <remarks>
/// Deliberately the EXISTING template XML format rather than a new one: a team can copy a template
/// out of the WatchList to start from, and the validator already understands what comes back.
/// An action in one of these names its machine with the generic <see cref="AgentToken"/>; the
/// builder rewrites that to <c>[_Agent1]</c>, <c>[_Agent2]</c>... when it expands the stage.
/// </remarks>
public sealed class FolderStageTemplateSource(string templatesRoot) : IStageTemplateSource
{
    /// <summary>The placeholder a stage template uses for "whichever agent this copy runs on".</summary>
    public const string AgentToken = "[_Agent]";

    public StageTemplateResult GetTemplates()
    {
        var templates = new List<TemplateConfig>();
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(templatesRoot) || !Directory.Exists(templatesRoot))
            return new StageTemplateResult(templates, problems);

        foreach (var path in Directory
            .EnumerateFiles(templatesRoot, "*.xml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            List<TemplateConfig> parsed;
            try
            {
                parsed = WatchListXmlParser.ParseTemplatesFromXml(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                problems.Add($"'{name}' could not be parsed as template XML: {ex.Message}");
                continue;
            }

            if (parsed.Count == 0)
            {
                problems.Add($"'{name}' defines no templates.");
                continue;
            }

            foreach (var template in parsed)
            {
                if (templates.Any(t => string.Equals(t.ID, template.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"Stage template '{template.ID}' is defined more than once.");
                    continue;
                }

                templates.Add(template);
            }
        }

        return new StageTemplateResult(templates, problems);
    }
}
