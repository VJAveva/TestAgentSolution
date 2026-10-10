using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// <see cref="PipelineBuilderService.Preview"/> is the whole correctness surface of the builder: it
/// decides what a generated pipeline looks like before anything is written. The properties pinned
/// here are the ones that are expensive to get wrong - a tag that breaks REST routing, a fan-out
/// that reuses NodeIds, a config that carries a secret forward, or output the validator rejects.
/// </summary>
public class PipelineBuilderPreviewTests : IDisposable
{
    private readonly string _root;
    private readonly string _parameters;
    private readonly string _recipes;
    private readonly string _templates;

    public PipelineBuilderPreviewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"Builder_{Guid.NewGuid():N}");
        _parameters = Path.Combine(_root, "Parameters");
        _recipes = Path.Combine(_root, "recipes");
        _templates = Path.Combine(_root, "templates");
        Directory.CreateDirectory(Path.Combine(_parameters, "SP2026R2"));
        Directory.CreateDirectory(_recipes);
        Directory.CreateDirectory(_templates);

        File.WriteAllText(Path.Combine(_parameters, "SP2026R2", "SP2026R2-pipeline-config.json"), """
        {
          "version": 1,
          "global": {
            "_ReleaseName": "SP2026R2",
            "_Installer": "C:\\TestSetup\\SP2026R2\\setup.exe",
            "_BuildNumber": "should-not-carry",
            "_VCloudPassword": "super-secret"
          },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } }
        }
        """);

        File.WriteAllText(Path.Combine(_recipes, "smoke.json"), """
        {
          "name": "SmokeE2E",
          "stages": [
            { "name": "Prepare", "templateId": "Prepare", "agentMapping": "pool-fanout" },
            { "name": "Install", "templateId": "Install", "agentMapping": "ordered-first-then-rest" },
            { "name": "Smoke",   "templateId": "Smoke",   "agentMapping": "single" }
          ]
        }
        """);

        File.WriteAllText(Path.Combine(_templates, "stages.xml"), """
        <Templates>
          <Template ID="Prepare">
            <Action Type="RunRemoteCommand" Tag="prep [_Agent]" AgentName="[_Agent]"
                    Command="C:\Scripts\Prepare.bat" Parameters="[_Agent] [_ReleaseName]" Timeout="600" />
          </Template>
          <Template ID="Install">
            <Action Type="RunRemoteCommand" Tag="install" AgentName="[_Agent]"
                    Command="C:\Scripts\Install.bat" Timeout="600" />
          </Template>
          <Template ID="Smoke">
            <Action Type="RunRemoteCommand" Tag="smoke" AgentName="[_Agent]"
                    Command="C:\Scripts\Smoke.bat" Timeout="600" />
          </Template>
        </Templates>
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private PipelineBuilderService Service() => new(
        new DerivedTargetCatalog(_parameters),
        new FolderRecipeSource(_recipes),
        new FolderStageTemplateSource(_templates),
        BuilderAuth.AllowAll,
        Path.Combine(_root, "Triggers"));

    private static BuilderRequest Request(int agentCount) => new(
        "SP2026R2-pipeline-config",
        "SmokeE2E",
        [.. Enumerable.Range(1, agentCount).Select(i => $"agent{i}")]);

    private static List<ActionConfig> ActionsOf(IActionNode node)
    {
        var found = new List<ActionConfig>();
        Walk(node);
        return found;

        void Walk(IActionNode n)
        {
            switch (n)
            {
                case ActionConfig a: found.Add(a); break;
                case ActionGroupConfig g: foreach (var c in g.Children) Walk(c); break;
            }
        }
    }

    private static List<IActionNode> AllNodes(WatchItemConfig item)
    {
        var found = new List<IActionNode>();
        foreach (var ev in item.Events) foreach (var child in ev.Children) Walk(child);
        return found;

        void Walk(IActionNode n)
        {
            found.Add(n);
            if (n is ActionGroupConfig g) foreach (var c in g.Children) Walk(c);
        }
    }

    // ── shape ───────────────────────────────────────────────────────────

    [Fact]
    public void Preview_Should_ProduceAStageGroupPerRecipeStage()
    {
        var result = Service().Preview(Request(3));

        Assert.False(result.HasErrors);
        var children = result.WatchItem!.Events.Single().Children;
        Assert.IsType<InitializeConfig>(children[0]);
        Assert.Equal(
            ["Prepare", "Install", "Smoke"],
            children.OfType<ActionGroupConfig>().Select(g => g.Tag));
    }

    [Fact]
    public void Preview_Should_FanOutOneCopyPerAgent_When_MappingIsPoolFanout()
    {
        var result = Service().Preview(Request(50));

        var prepare = result.WatchItem!.Events.Single().Children
            .OfType<ActionGroupConfig>().Single(g => g.Tag == "Prepare");

        Assert.Equal(ExecutionMode.Parallel, prepare.ExecutionType);
        Assert.Equal(50, prepare.Children.Count);
        Assert.Equal(50, ActionsOf(prepare).Count);
        Assert.Equal("[_Agent1]", ActionsOf(prepare)[0].AgentName);
        Assert.Equal("[_Agent50]", ActionsOf(prepare)[49].AgentName);
    }

    [Fact]
    public void Preview_Should_RunFirstAgentAlone_When_MappingIsOrderedFirstThenRest()
    {
        var result = Service().Preview(Request(4));

        var install = result.WatchItem!.Events.Single().Children
            .OfType<ActionGroupConfig>().Single(g => g.Tag == "Install");

        Assert.Equal(ExecutionMode.Sequential, install.ExecutionType);
        var first = Assert.IsType<ActionGroupConfig>(install.Children[0]);
        var rest = Assert.IsType<ActionGroupConfig>(install.Children[1]);

        Assert.Equal("[_Agent1]", ActionsOf(first).Single().AgentName);
        Assert.Equal(ExecutionMode.Parallel, rest.ExecutionType);
        Assert.Equal(
            ["[_Agent2]", "[_Agent3]", "[_Agent4]"],
            ActionsOf(rest).Select(a => a.AgentName));
    }

    [Fact]
    public void Preview_Should_UseOnlyTheFirstAgent_When_MappingIsSingle()
    {
        var result = Service().Preview(Request(5));

        var smoke = result.WatchItem!.Events.Single().Children
            .OfType<ActionGroupConfig>().Single(g => g.Tag == "Smoke");

        Assert.Equal("[_Agent1]", ActionsOf(smoke).Single().AgentName);
    }

    [Fact]
    public void Preview_Should_RewriteTheAgentToken_EverywhereItAppears()
    {
        // The token is not only in AgentName - a command line can name the machine too.
        var result = Service().Preview(Request(2));

        var prepare = result.WatchItem!.Events.Single().Children
            .OfType<ActionGroupConfig>().Single(g => g.Tag == "Prepare");
        var second = ActionsOf(prepare)[1];

        Assert.Equal("[_Agent2] [_ReleaseName]", second.Parameters);
        Assert.Equal("prep [_Agent2]", second.Tag);
        Assert.DoesNotContain(ActionsOf(prepare), a => a.Parameters.Contains("[_Agent]"));
    }

    [Fact]
    public void Preview_Should_GiveEveryNodeADistinctNodeId()
    {
        // DeepClone preserves NodeId, so without a fresh id per copy a 50-way fan-out would have 50
        // nodes sharing one id and progress would paint the wrong copy.
        var result = Service().Preview(Request(20));

        var ids = AllNodes(result.WatchItem!).Select(n => n.NodeId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ── generated config ────────────────────────────────────────────────

    [Fact]
    public void Preview_Should_WriteTheChosenAgentsAsAProfile()
    {
        var result = Service().Preview(Request(3));

        var config = System.Text.Json.JsonSerializer.Deserialize<PipelineParameterConfig>(
            result.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var profile = config.Profiles["SmokeE2E"];
        Assert.Equal("agent1", profile["_Agent1"]);
        Assert.Equal("agent3", profile["_Agent3"]);
    }

    [Fact]
    public void Preview_Should_CarryTargetPaths_ButNotBuildOrSecrets()
    {
        var result = Service().Preview(Request(2));

        Assert.Contains("_Installer", result.ConfigJson);
        Assert.DoesNotContain("_BuildNumber", result.ConfigJson);
        Assert.DoesNotContain("super-secret", result.ConfigJson);
        Assert.DoesNotContain("_VCloudPassword", result.ConfigJson);
    }

    [Fact]
    public void Preview_Should_PointInitializeAtTheGeneratedConfigAndProfile()
    {
        var result = Service().Preview(Request(2));

        var init = result.WatchItem!.Events.Single().Children.OfType<InitializeConfig>().Single();

        Assert.EndsWith(result.ConfigFileName, init.ParameterFile);
        Assert.Equal("SmokeE2E", init.Profile);
    }

    // ── identity + trigger ──────────────────────────────────────────────

    [Fact]
    public void Preview_Should_GenerateARoutingSafeTag()
    {
        var result = Service().Preview(Request(2));

        Assert.Equal("SP2026R2-SmokeE2E", result.Tag);
        Assert.True(PipelineTag.IsValid(result.Tag));
        Assert.Equal("SP2026R2 - SmokeE2E", result.Title);
    }

    [Theory]
    [InlineData("Revert 9 Nodes + Install SP2023R2SP2", "Revert-9-Nodes-Install-SP2023R2SP2")]
    [InlineData("  spaces  everywhere  ", "spaces-everywhere")]
    [InlineData("%%%", "pipeline")]
    public void Sanitize_Should_StripEverythingThatBreaksRoutingOrIdentity(string raw, string expected)
    {
        // '+' in a tag once 404'd every tag-in-path endpoint, because IIS rejects the encoded form.
        var tag = PipelineTag.Sanitize(raw);

        Assert.Equal(expected, tag);
        Assert.True(PipelineTag.IsValid(tag));
    }

    [Fact]
    public void Sanitize_Should_StayWithinTheAssignmentColumnWidth()
    {
        var tag = PipelineTag.Sanitize(new string('a', 200));

        Assert.Equal(PipelineTag.MaxLength, tag.Length);
        Assert.True(PipelineTag.IsValid(tag));
    }

    [Fact]
    public void Preview_Should_SetTheTriggerFolderAndFilter()
    {
        var result = Service().Preview(Request(2));

        Assert.EndsWith(Path.Combine("Triggers", "SP2026R2"), result.TriggerFolder);
        Assert.Equal("SmokeE2E.txt", result.TriggerFilter);
        Assert.Equal(result.TriggerFolder, result.WatchItem!.Path);
        Assert.Equal(result.TriggerFilter, result.WatchItem.Filter);
    }

    // ── it has to pass the real validator ───────────────────────────────

    [Fact]
    public void Preview_Should_ProduceAWatchItemTheValidatorAccepts()
    {
        var result = Service().Preview(Request(5));

        var config = new WatchListConfig { WatchItems = [result.WatchItem!] };
        var errors = WatchListValidator.Analyze(config)
            .Where(i => i.Severity == WatchIssueSeverity.Error)
            .Select(i => i.Message)
            .ToList();

        Assert.True(errors.Count == 0, "validator errors: " + string.Join(" | ", errors));
    }

    [Fact]
    public void Preview_Should_ContainNoRefs_So_ImportCannotCollideWithALiveTemplate()
    {
        var result = Service().Preview(Request(3));

        Assert.DoesNotContain(AllNodes(result.WatchItem!), n => n is RefConfig);
    }

    // ── failure paths write nothing and say why ─────────────────────────

    [Fact]
    public void Preview_Should_Error_When_TheRecipeNamesAnUnknownTemplate()
    {
        File.WriteAllText(Path.Combine(_recipes, "broken.json"), """
        { "name": "Broken", "stages": [ { "name": "Ghost", "templateId": "NoSuchTemplate", "agentMapping": "single" } ] }
        """);

        var result = Service().Preview(new BuilderRequest("SP2026R2-pipeline-config", "Broken", ["agent1"]));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains("NoSuchTemplate"));
    }

    [Theory]
    [InlineData("NoSuchTarget", "SmokeE2E")]
    [InlineData("SP2026R2-pipeline-config", "NoSuchRecipe")]
    public void Preview_Should_Error_When_TargetOrRecipeIsUnknown(string targetId, string recipeName)
    {
        var result = Service().Preview(new BuilderRequest(targetId, recipeName, ["agent1"]));

        Assert.True(result.HasErrors);
        Assert.Null(result.WatchItem);
    }

    [Fact]
    public void Preview_Should_Error_When_NoAgentsAreChosen()
    {
        var result = Service().Preview(new BuilderRequest("SP2026R2-pipeline-config", "SmokeE2E", []));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains("No agents"));
    }

    [Fact]
    public void Preview_Should_WriteNothingToDisk()
    {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length;

        Service().Preview(Request(10));

        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
    }

    // ── a team adds a recipe by dropping a file ─────────────────────────

    [Fact]
    public void ASecondRecipe_Should_Work_When_DroppedInWithNoCodeChange()
    {
        File.WriteAllText(Path.Combine(_recipes, "install-only.json"), """
        { "name": "InstallOnly", "stages": [ { "name": "Install", "templateId": "Install", "agentMapping": "pool-fanout" } ] }
        """);

        var result = Service().Preview(new BuilderRequest("SP2026R2-pipeline-config", "InstallOnly", ["a", "b"]));

        Assert.False(result.HasErrors);
        Assert.Equal("SP2026R2-InstallOnly", result.Tag);
        Assert.Single(result.WatchItem!.Events.Single().Children.OfType<ActionGroupConfig>());
    }

    [Theory]
    [InlineData("pool-fanout", AgentMapping.PoolFanout)]
    [InlineData("PoolFanout", AgentMapping.PoolFanout)]
    [InlineData("ordered-first-then-rest", AgentMapping.OrderedFirstThenRest)]
    [InlineData("single", AgentMapping.Single)]
    public void TryParseAgentMapping_Should_AcceptTheSpellingRecipeFilesUse(string raw, AgentMapping expected)
    {
        Assert.True(FolderRecipeSource.TryParseAgentMapping(raw, out var mapping));
        Assert.Equal(expected, mapping);
    }

    [Fact]
    public void GetRecipes_Should_ReportAnUnknownMapping_RatherThanGuess()
    {
        File.WriteAllText(Path.Combine(_recipes, "bad-mapping.json"), """
        { "name": "BadMapping", "stages": [ { "name": "S", "templateId": "Install", "agentMapping": "sprinkle" } ] }
        """);

        var result = new FolderRecipeSource(_recipes).GetRecipes();

        Assert.DoesNotContain(result.Recipes, r => r.Name == "BadMapping");
        Assert.Contains(result.Problems, p => p.Contains("sprinkle"));
    }
}
