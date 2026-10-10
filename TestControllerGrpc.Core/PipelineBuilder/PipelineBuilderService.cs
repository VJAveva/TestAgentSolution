using System.Reflection;
using System.Text;
using System.Text.Json;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// Generates a WatchItem Tag that is safe to use as an identity key.
/// </summary>
/// <remarks>
/// The Tag is not just a label: it keys <c>PipelineAssignments.PipelineId</c>, it is the audit
/// <c>ResourceId</c>, and it travels in REST paths. A '+' in a Tag once 404'd every tag-in-path
/// endpoint because IIS rejects the encoded separator, so the charset is deliberately narrow.
/// </remarks>
public static class PipelineTag
{
    /// <summary>Matches the declared width of <c>PipelineAssignments.PipelineId</c>.</summary>
    public const int MaxLength = 36;

    public static bool IsValid(string? tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && tag.Length <= MaxLength
        && tag.All(IsAllowed);

    private static bool IsAllowed(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    /// <summary>Folds anything unsafe to '-', collapses runs, and trims to <see cref="MaxLength"/>.</summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "pipeline";

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (IsAllowed(c))
            {
                sb.Append(c);
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var cleaned = sb.ToString().Trim('-', '.', '_');
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].Trim('-', '.', '_');

        return cleaned.Length == 0 ? "pipeline" : cleaned;
    }
}

public enum BuilderSeverity { Info, Warning, Error }

public sealed record BuilderIssue(BuilderSeverity Severity, string Message);

public sealed record BuilderRequest(
    string TargetId,
    string RecipeName,
    IReadOnlyList<string> AgentNames)
{
    /// <summary>Profile name written into the generated config. Defaults to the recipe name.</summary>
    public string? ProfileName { get; init; }
}

public sealed record BuilderResult(
    string Tag,
    string Title,
    string ConfigFileName,
    string ConfigJson,
    PipelineParameterConfig Config,
    string ConfigPath,
    WatchItemConfig? WatchItem,
    IReadOnlyList<string> StageTemplatesUsed,
    string TriggerFolder,
    string TriggerFilter,
    IReadOnlyList<BuilderIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == BuilderSeverity.Error);
}

/// <summary>
/// Turns a recipe + target + agent list into the files a pipeline needs. <see cref="Preview"/> is
/// pure: it writes nothing and touches no live state.
/// </summary>
/// <remarks>
/// Generated pipelines are fully EXPANDED - every stage becomes concrete nodes and nothing is a
/// <c>Ref</c>. That is a deliberate simplification: a self-contained fragment carries no templates,
/// so importing one can never collide with a live template of the same ID, and can never repoint
/// the pipelines that share it.
/// <para>
/// <paramref name="authorization"/> is REQUIRED, not optional. An optional authorizer that defaults
/// to null silently turns the permission off for any host that forgets to pass one - the same shape
/// that made pipeline locking a no-op when a host skipped registering the lock registry.
/// </para>
/// </remarks>
public sealed partial class PipelineBuilderService(
    ITargetCatalog targets,
    IRecipeSource recipes,
    IStageTemplateSource stageTemplates,
    IAuthorizationService authorization,
    string triggerRoot)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Cached: this runs once per cloned node during fan-out.</summary>
    private static readonly PropertyInfo[] WritableStrings =
        [.. typeof(ActionConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite
                        && p.Name != nameof(ActionConfig.NodeId))];

    /// <summary>Targets the user can choose from. Re-read each call so a dropped-in config shows up.</summary>
    public TargetCatalogResult Targets() => targets.GetTargets();

    /// <summary>Recipes the user can choose from.</summary>
    public RecipeCatalogResult Recipes() => recipes.GetRecipes();

    public BuilderResult Preview(BuilderRequest request)
    {
        var issues = new List<BuilderIssue>();

        var target = targets.GetTargets().Targets            .FirstOrDefault(t => string.Equals(t.Id, request.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            issues.Add(new BuilderIssue(BuilderSeverity.Error, $"Unknown target '{request.TargetId}'."));

        var recipe = recipes.GetRecipes().Recipes
            .FirstOrDefault(r => string.Equals(r.Name, request.RecipeName, StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
            issues.Add(new BuilderIssue(BuilderSeverity.Error, $"Unknown recipe '{request.RecipeName}'."));

        var agents = request.AgentNames?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? [];
        if (agents.Count == 0)
            issues.Add(new BuilderIssue(BuilderSeverity.Error, "No agents were chosen."));

        var profileName = string.IsNullOrWhiteSpace(request.ProfileName)
            ? request.RecipeName
            : request.ProfileName;

        var tag = PipelineTag.Sanitize($"{target?.ReleaseName ?? request.TargetId}-{request.RecipeName}");
        var title = $"{target?.ReleaseName ?? request.TargetId} - {request.RecipeName}";
        var configFileName = $"{tag}-pipeline-config.json";
        var triggerFolder = Path.Combine(triggerRoot, target?.ReleaseName ?? request.TargetId);
        var triggerFilter = $"{PipelineTag.Sanitize(request.RecipeName)}.txt";

        if (target is null || recipe is null || agents.Count == 0)
        {
            return new BuilderResult(
                tag, title, configFileName, "", new PipelineParameterConfig(), "",
                null, [], triggerFolder, triggerFilter, issues);
        }

        var library = stageTemplates.GetTemplates();
        foreach (var problem in library.Problems)
            issues.Add(new BuilderIssue(BuilderSeverity.Warning, problem));

        var config = BuildConfig(target, profileName!, agents);
        var configJson = JsonSerializer.Serialize(config, WriteOptions);
        var configPath = Path.Combine(Path.GetDirectoryName(target.ConfigPath) ?? "", configFileName);

        var children = new List<IActionNode>
        {
            new InitializeConfig
            {
                Tag = "Initialize",
                ParameterFile = configPath,
                Profile = profileName!,
            },
        };

        var used = new List<string>();
        foreach (var stage in recipe.Stages)
        {
            var template = library.Templates
                .FirstOrDefault(t => string.Equals(t.ID, stage.TemplateId, StringComparison.OrdinalIgnoreCase));

            if (template is null)
            {
                issues.Add(new BuilderIssue(BuilderSeverity.Error,
                    $"Stage '{stage.Name}' names template '{stage.TemplateId}', which is not in the template library."));
                continue;
            }

            used.Add(template.ID);
            children.Add(ExpandStage(stage, template, agents.Count));
        }

        var watchItem = new WatchItemConfig
        {
            Tag = tag,
            Path = triggerFolder,
            Filter = triggerFilter,
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    ExecutionType = ExecutionMode.Sequential,
                    Children = children,
                },
            ],
        };

        if (!PipelineTag.IsValid(tag))
            issues.Add(new BuilderIssue(BuilderSeverity.Error, $"Generated tag '{tag}' is not a safe identity key."));

        return new BuilderResult(
            tag, title, configFileName, configJson, config, configPath,
            watchItem, used, triggerFolder, triggerFilter, issues);
    }

    private static PipelineParameterConfig BuildConfig(
        TargetDescriptor target, string profileName, IReadOnlyList<string> agents)
    {
        var profile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < agents.Count; i++)
            profile[$"_Agent{i + 1}"] = agents[i];

        return new PipelineParameterConfig
        {
            Version = 1,
            Global = new Dictionary<string, string>(target.Values, StringComparer.OrdinalIgnoreCase),
            Profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [profileName] = profile,
            },
        };
    }

    private static ActionGroupConfig ExpandStage(RecipeStage stage, TemplateConfig template, int agentCount)
        => stage.AgentMapping switch
        {
            AgentMapping.Single => Group(stage.Name, ExecutionMode.Sequential,
                [CopyForAgent(stage, template, 1)]),

            AgentMapping.PoolFanout => Group(stage.Name, ExecutionMode.Parallel,
                [.. Enumerable.Range(1, agentCount).Select(i => CopyForAgent(stage, template, i))]),

            AgentMapping.OrderedFirstThenRest => Group(stage.Name, ExecutionMode.Sequential,
            [
                CopyForAgent(stage, template, 1),
                Group($"{stage.Name} - rest", ExecutionMode.Parallel,
                    [.. Enumerable.Range(2, Math.Max(0, agentCount - 1)).Select(i => CopyForAgent(stage, template, i))]),
            ]),

            _ => Group(stage.Name, ExecutionMode.Sequential, []),
        };

    private static ActionGroupConfig Group(string tag, ExecutionMode mode, List<IActionNode> children) =>
        new() { Tag = tag, ExecutionType = mode, Children = children };

    /// <summary>One copy of a stage template bound to agent slot <paramref name="index"/>.</summary>
    private static IActionNode CopyForAgent(RecipeStage stage, TemplateConfig template, int index)
    {
        var group = Group(
            $"{stage.Name} - agent {index}",
            ExecutionMode.Sequential,
            [.. template.Children.Select(c => c.DeepClone())]);

        Rewrite(group, $"[_Agent{index}]");
        return group;
    }

    private static void Rewrite(IActionNode node, string agentToken)
    {
        // DeepClone keeps the ORIGINAL NodeId, so N copies of a stage would all share one id and the
        // tree would resolve progress onto the wrong copy. Every clone gets a fresh id here.
        switch (node)
        {
            case ActionGroupConfig group:
                group.NodeId = Guid.NewGuid().ToString("N");
                foreach (var child in group.Children) Rewrite(child, agentToken);
                break;

            case ActionConfig action:
                action.NodeId = Guid.NewGuid().ToString("N");
                foreach (var property in WritableStrings)
                {
                    if (property.GetValue(action) is not string value
                        || !value.Contains(FolderStageTemplateSource.AgentToken, StringComparison.Ordinal))
                        continue;

                    property.SetValue(action, value.Replace(
                        FolderStageTemplateSource.AgentToken, agentToken, StringComparison.Ordinal));
                }
                break;

            case InitializeConfig init:
                init.NodeId = Guid.NewGuid().ToString("N");
                break;

            case RefConfig reference:
                reference.NodeId = Guid.NewGuid().ToString("N");
                break;
        }
    }
}
