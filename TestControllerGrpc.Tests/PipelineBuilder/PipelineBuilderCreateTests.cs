using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.PipelineBuilder;

/// <summary>
/// Validate is a SPLIT gate and Create is the first thing that touches disk, so these pin the two
/// properties that make that safe: a pipeline with no build selected is still creatable (the build
/// is late-bound by design), and nothing is written unless the authoring gate is green.
/// </summary>
public class PipelineBuilderCreateTests : IDisposable
{
    private readonly string _root;
    private readonly string _parameters;
    private readonly string _recipes;
    private readonly string _templates;
    private readonly string _fragments;

    public PipelineBuilderCreateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"BuilderCreate_{Guid.NewGuid():N}");
        _parameters = Path.Combine(_root, "Parameters");
        _recipes = Path.Combine(_root, "recipes");
        _templates = Path.Combine(_root, "templates");
        _fragments = Path.Combine(_root, "fragments");
        Directory.CreateDirectory(Path.Combine(_parameters, "SP2026R2"));
        Directory.CreateDirectory(_recipes);
        Directory.CreateDirectory(_templates);

        File.WriteAllText(Path.Combine(_parameters, "SP2026R2", "SP2026R2-pipeline-config.json"), """
        {
          "version": 1,
          "global": {
            "_ReleaseName": "SP2026R2",
            "_Installer": "C:\\TestSetup\\SP2026R2\\setup.exe",
            "_BuildNumber": "from-release-config",
            "_DropLocation": "\\\\drop\\old"
          },
          "profiles": { "Sanity": { "_Agent1": "jvgr1" } }
        }
        """);

        File.WriteAllText(Path.Combine(_recipes, "smoke.json"), """
        { "name": "SmokeE2E", "stages": [ { "name": "Install", "templateId": "Install", "agentMapping": "pool-fanout" } ] }
        """);

        // Uses [_BuildNumber] on purpose: it is NOT in the generated config, because the build is
        // supplied later by GlobalVariables or the trigger.
        File.WriteAllText(Path.Combine(_templates, "stages.xml"), """
        <Templates>
          <Template ID="Install">
            <Action Type="RunRemoteCommand" Tag="install" AgentName="[_Agent]"
                    Command="C:\Scripts\Install.bat" Parameters="[_BuildNumber] [_ReleaseName]" Timeout="600" />
          </Template>
          <Template ID="Undefined">
            <Action Type="RunRemoteCommand" Tag="oops" AgentName="[_Agent]"
                    Command="C:\Scripts\Run.bat" Parameters="[_NobodyDefinesThis]" Timeout="600" />
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

    private Task<CreateOutcome> CreateAsAdmin(BuilderResult preview, BuilderValidation validation) =>
        Service().CreateAsync(preview, validation, _fragments, BuilderAuth.Admin);

    private BuilderResult Preview(string recipe = "SmokeE2E") =>
        Service().Preview(new BuilderRequest("SP2026R2-pipeline-config", recipe, ["agentA", "agentB"]));

    private static WatchListConfig Live(params WatchItemConfig[] items) => new() { WatchItems = [.. items] };

    // ── the split gate ──────────────────────────────────────────────────

    [Fact]
    public void Validate_Should_Pass_When_OnlyTheBuildIsUndefined()
    {
        // THE point of splitting the gate: a newly authored pipeline has no build by design, so
        // gating on [_BuildNumber] would leave Create disabled forever.
        var preview = Preview();

        var validation = Service().Validate(preview, Live());

        Assert.True(validation.CanCreate, "blocked by: " + string.Join(" | ", validation.Blocking.Select(b => b.Message)));
        Assert.Contains(validation.Informational, i => i.Message.Contains("_BuildNumber"));
        Assert.Contains(validation.Informational, i => i.Message.Contains("supplied at run time"));
    }

    [Fact]
    public void Validate_Should_Block_When_ATokenNoLayerDefinesIsUsed()
    {
        File.WriteAllText(Path.Combine(_recipes, "undefined.json"), """
        { "name": "Undef", "stages": [ { "name": "S", "templateId": "Undefined", "agentMapping": "single" } ] }
        """);
        var preview = Preview("Undef");

        var validation = Service().Validate(preview, Live());

        Assert.False(validation.CanCreate);
        Assert.Contains(validation.Blocking, b => b.Message.Contains("_NobodyDefinesThis"));
    }

    [Fact]
    public void Validate_Should_Block_When_TheTagAlreadyExists()
    {
        var preview = Preview();
        var live = Live(new WatchItemConfig { Tag = preview.Tag, Path = @"C:\Elsewhere", Filter = "x.txt" });

        var validation = Service().Validate(preview, live);

        Assert.False(validation.CanCreate);
        Assert.Contains(validation.Blocking, b => b.Message.Contains("already exists"));
    }

    [Fact]
    public void Validate_Should_Block_When_AnotherPipelineWatchesTheSameTriggerFile()
    {
        var preview = Preview();
        var live = Live(new WatchItemConfig
        {
            Tag = "Existing",
            Path = preview.TriggerFolder,
            Filter = preview.TriggerFilter,
        });

        var validation = Service().Validate(preview, live);

        Assert.False(validation.CanCreate);
        Assert.Contains(validation.Blocking, b => b.Message.Contains("would both fire"));
    }

    [Fact]
    public void Validate_Should_Block_When_TheGeneratedConfigAlreadyExists()
    {
        var preview = Preview();
        File.WriteAllText(preview.ConfigPath, "{}");

        var validation = Service().Validate(preview, Live());

        Assert.False(validation.CanCreate);
        Assert.Contains(validation.Blocking, b => b.Message.Contains(preview.ConfigFileName));
    }

    // ── Create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Should_WriteTheConfigFragmentAndTriggerFolder()
    {
        var preview = Preview();
        var validation = Service().Validate(preview, Live());

        var outcome = await CreateAsAdmin(preview, validation);

        Assert.True(outcome.Written);
        Assert.True(File.Exists(outcome.ConfigPath));
        Assert.True(File.Exists(outcome.FragmentPath));
        Assert.True(Directory.Exists(outcome.TriggerFolder), "the run fails before any action if the watch folder is absent");
    }

    [Fact]
    public async Task Create_Should_WriteNothing_When_ValidationIsNotGreen()
    {
        var preview = Preview();
        var blocked = new BuilderValidation([new BuilderIssue(BuilderSeverity.Error, "nope")], []);

        var outcome = await CreateAsAdmin(preview, blocked);

        Assert.False(outcome.Written);
        Assert.False(File.Exists(preview.ConfigPath));
        Assert.False(Directory.Exists(_fragments));
    }

    // ── the service enforces the permission itself ──────────────────────

    [Fact]
    public async Task Create_Should_Refuse_When_TheCallerLacksPipelineAuthor()
    {
        // Enforced at the SERVICE, not just the view model: the web path reaches this from another
        // process, where no view model gate exists at all.
        var service = new PipelineBuilderService(
            new DerivedTargetCatalog(_parameters),
            new FolderRecipeSource(_recipes),
            new FolderStageTemplateSource(_templates),
            BuilderAuth.AdminOnly,
            Path.Combine(_root, "Triggers"));

        var preview = Preview();
        var validation = service.Validate(preview, Live());

        var outcome = await service.CreateAsync(preview, validation, _fragments, BuilderAuth.Engineer);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(preview.ConfigPath));
        Assert.False(Directory.Exists(_fragments));
    }

    [Fact]
    public async Task Create_Should_Write_When_AnAdministratorCalls()
    {
        var service = new PipelineBuilderService(
            new DerivedTargetCatalog(_parameters),
            new FolderRecipeSource(_recipes),
            new FolderStageTemplateSource(_templates),
            BuilderAuth.AdminOnly,
            Path.Combine(_root, "Triggers"));

        var preview = Preview();
        var outcome = await service.CreateAsync(
            preview, service.Validate(preview, Live()), _fragments, BuilderAuth.Admin);

        Assert.True(outcome.Written);
    }

    [Fact]
    public async Task Create_Should_CheckPermissionBeforeValidation_So_ADeniedCallerLearnsNothingElse()
    {
        var service = new PipelineBuilderService(
            new DerivedTargetCatalog(_parameters),
            new FolderRecipeSource(_recipes),
            new FolderStageTemplateSource(_templates),
            BuilderAuth.AdminOnly,
            Path.Combine(_root, "Triggers"));

        var preview = Preview();
        var blocked = new BuilderValidation([new BuilderIssue(BuilderSeverity.Error, "secret detail")], []);

        var outcome = await service.CreateAsync(preview, blocked, _fragments, BuilderAuth.Engineer);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(outcome.Issues, i => i.Message.Contains("secret detail"));
    }

    // ── tag rules are re-checked at the write boundary ──────────────────

    [Theory]
    [InlineData("has spaces")]
    [InlineData("plus+sign")]
    [InlineData("percent%")]
    public async Task Create_Should_Refuse_When_TheTagCharsetIsUnsafe(string badTag)
    {
        var preview = Preview() with { Tag = badTag };
        var validation = new BuilderValidation([], []);

        var outcome = await CreateAsAdmin(preview, validation);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("safe identity key"));
    }

    [Fact]
    public async Task Create_Should_Refuse_When_TheTagExceedsTheAssignmentColumn()
    {
        var preview = Preview() with { Tag = new string('a', PipelineTag.MaxLength + 1) };

        var outcome = await CreateAsAdmin(preview, new BuilderValidation([], []));

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("safe identity key"));
    }

    [Fact]
    public async Task Create_Should_LeaveTheReleaseConfigUntouched()
    {
        var releaseConfig = Path.Combine(_parameters, "SP2026R2", "SP2026R2-pipeline-config.json");
        var before = File.ReadAllText(releaseConfig);
        var preview = Preview();

        await CreateAsAdmin(preview, Service().Validate(preview, Live()));

        Assert.Equal(before, File.ReadAllText(releaseConfig));
    }

    [Fact]
    public async Task Create_Should_ProduceAFragmentThatParsesBackToTheSamePipeline()
    {
        var preview = Preview();
        var outcome = await CreateAsAdmin(preview, Service().Validate(preview, Live()));

        var (items, templates) = WatchListXmlParser.ParseWatchItemsFromXml(File.ReadAllText(outcome.FragmentPath));

        var reparsed = Assert.Single(items);
        Assert.Equal(preview.Tag, reparsed.Tag);
        Assert.Equal(preview.TriggerFilter, reparsed.Filter);
        Assert.Empty(templates);
    }

    [Fact]
    public async Task Create_Should_WriteAConfigTheResolverCanLoad()
    {
        var preview = Preview();
        var outcome = await CreateAsAdmin(preview, Service().Validate(preview, Live()));

        var ctx = new PipelineExecutionContext { WatchItemTag = preview.Tag };
        var failure = ParameterResolver.TryLoadInitializeSource(ctx, outcome.ConfigPath, "SmokeE2E", preview.Tag);

        Assert.Null(failure);
        Assert.Equal("agentA", ctx.Parameters["_Agent1"]);
        Assert.Equal("SP2026R2", ctx.Parameters["_ReleaseName"]);
    }

    // ── import conflicts ────────────────────────────────────────────────

    private static TemplateConfig Template(string id, string command) => new()
    {
        ID = id,
        Children = [new ActionConfig { Type = ActionType.RunRemoteCommand, Tag = "t", AgentName = "[_Agent1]", Command = command }],
    };

    [Fact]
    public void Plan_Should_ReuseATemplate_When_ContentIsIdentical()
    {
        var live = new WatchListConfig { Templates = [Template("Shared", @"C:\a.bat")] };

        var plan = WatchListFragmentImport.Plan(live, [], [Template("Shared", @"C:\a.bat")]);

        Assert.True(plan.CanImport);
        Assert.Equal(["Shared"], plan.ReusableTemplates);
        Assert.Empty(plan.NewTemplates);
    }

    [Fact]
    public void Plan_Should_Conflict_When_ATemplateIdMatchesButContentDiffers()
    {
        // The dangerous case: silently reusing the live one repoints every pipeline sharing it.
        var live = new WatchListConfig { Templates = [Template("Shared", @"C:\a.bat")] };

        var plan = WatchListFragmentImport.Plan(live, [], [Template("Shared", @"C:\DIFFERENT.bat")]);

        Assert.False(plan.CanImport);
        Assert.Empty(plan.ReusableTemplates);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ImportConflictKind.TemplateContentDiffers, conflict.Kind);
    }

    [Fact]
    public void Plan_Should_Conflict_When_TheTagAlreadyExists()
    {
        var live = new WatchListConfig { WatchItems = [new WatchItemConfig { Tag = "Dup", Path = @"C:\T", Filter = "a.txt" }] };

        var plan = WatchListFragmentImport.Plan(
            live, [new WatchItemConfig { Tag = "Dup", Path = @"C:\Other", Filter = "b.txt" }], []);

        Assert.False(plan.CanImport);
        Assert.Equal(ImportConflictKind.DuplicateTag, Assert.Single(plan.Conflicts).Kind);
    }

    [Fact]
    public void Plan_Should_Conflict_When_TwoPipelinesWouldWatchOneTriggerFile()
    {
        // Two WatchItems legitimately SHARE a folder, so the collision is folder + filter together.
        var live = new WatchListConfig
        {
            WatchItems = [new WatchItemConfig { Tag = "First", Path = @"C:\Triggers\SP2026R2\", Filter = "Smoke.txt" }],
        };

        var plan = WatchListFragmentImport.Plan(
            live, [new WatchItemConfig { Tag = "Second", Path = @"C:\Triggers\SP2026R2", Filter = "Smoke.txt" }], []);

        Assert.Equal(ImportConflictKind.TriggerFilterCollision, Assert.Single(plan.Conflicts).Kind);
    }

    [Fact]
    public void Plan_Should_Allow_When_TheSameFolderUsesADifferentFilter()
    {
        var live = new WatchListConfig
        {
            WatchItems = [new WatchItemConfig { Tag = "First", Path = @"C:\Triggers\SP2026R2\", Filter = "Smoke.txt" }],
        };

        var plan = WatchListFragmentImport.Plan(
            live, [new WatchItemConfig { Tag = "Second", Path = @"C:\Triggers\SP2026R2\", Filter = "Warm.txt" }], []);

        Assert.True(plan.CanImport);
    }

    [Fact]
    public void Plan_Should_HaveNoTemplateConflicts_ForAGeneratedFragment()
    {
        // Generated fragments carry no templates at all, which is what makes importing one safe
        // against the live templates three pipelines already share.
        var preview = Preview();
        var live = new WatchListConfig { Templates = [Template("Install", @"C:\live.bat")] };

        var plan = WatchListFragmentImport.Plan(live, [preview.WatchItem!], []);

        Assert.True(plan.CanImport);
        Assert.Empty(plan.Conflicts);
    }
}
