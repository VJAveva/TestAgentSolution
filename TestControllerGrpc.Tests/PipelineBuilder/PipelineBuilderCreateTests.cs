using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Core.Preflight;
using Moq;
using System.Text.Json;

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

    private readonly BuilderAuth.RecordingAudit _audit = new();

    private PipelineBuilderService Service() => new(
        new DerivedTargetCatalog(_parameters),
        new FolderRecipeSource(_recipes),
        new FolderStageTemplateSource(_templates),
        BuilderAuth.AllowAll,
        _audit,
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

    [Fact]
    public void Validate_Should_ReviewSelectedProfileWithoutWriting_When_TheGeneratedFileDoesNotExist()
    {
        var preview = Preview();
        preview.Config.Profiles["Unused"] = new() { ["_Agent1"] = "wrong-agent" };
        preview.Config.Pipelines[preview.Tag] = new() { ["_ReleaseName"] = "PinnedRelease" };

        var review = Service().Validate(preview, Live());

        Assert.True(review.CanCreate);
        var agent = Assert.Single(review.Tokens, t => t.Name == "_Agent1");
        Assert.Equal("agentA", agent.DisplayValue);
        Assert.Equal("Profile", agent.Source);
        Assert.Contains("Group[Install]", agent.UsedBy);
        Assert.Contains("AgentName", agent.UsedBy);
        var release = Assert.Single(review.Tokens, t => t.Name == "_ReleaseName");
        Assert.Equal("PinnedRelease", release.DisplayValue);
        Assert.Equal("Pipeline", release.Source);
        Assert.Equal(BuilderTokenStatus.Runtime, Assert.Single(review.Tokens, t => t.Name == "_BuildNumber").Status);
        Assert.False(File.Exists(preview.ConfigPath));
        Assert.False(Directory.Exists(preview.TriggerFolder));
        Assert.False(Directory.Exists(_fragments));
    }

    [Fact]
    public void Validate_Should_BlockMissingToken_When_OnlyAnUnusedProfileDefinesIt()
    {
        var preview = Preview();
        preview.Config.Global.Remove("_ReleaseName");
        preview.Config.Profiles["Unused"] = new() { ["_ReleaseName"] = "not-selected" };

        var review = Service().Validate(preview, Live());

        Assert.False(review.CanCreate);
        Assert.Equal(BuilderTokenStatus.Missing, Assert.Single(review.Tokens, t => t.Name == "_ReleaseName").Status);
        Assert.Contains(review.Blocking, b => b.Message.Contains("_ReleaseName")
            && b.Message.Contains("Group[Install]") && b.Message.Contains("Action[install]")
            && b.Message.Contains("Parameters"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    public void Validate_Should_ReportOnlySourceError_When_SelectedProfileIsInvalid(string profile)
    {
        var preview = Preview();
        ParameterResolver.CollectInitializeNodes(preview.WatchItem!).Single().Profile = profile;

        var review = Service().Validate(preview, Live());

        Assert.False(review.CanCreate);
        Assert.Single(review.Blocking);
        Assert.Contains("Profile", review.Blocking[0].Message);
        Assert.Empty(review.Tokens);
    }

    [Theory]
    [InlineData("_Password")]
    [InlineData("_ApiKey")]
    [InlineData("_Credential")]
    [InlineData("_ConnectionString")]
    public void Validate_Should_MaskSecretValues_When_TokenReviewIsDisplayed(string key)
    {
        var preview = Preview();
        preview.Config.Global[key] = "test-only-sensitive-value";
        preview.WatchItem!.Events[0].Children.Add(new ActionConfig
        {
            Type = ActionType.RunCommand, Tag = "SecretProbe", Command = $"echo [{key}]",
        });

        var review = Service().Validate(preview, Live());

        Assert.True(review.CanCreate);
        Assert.Equal(TokenDisplay.Mask, Assert.Single(review.Tokens, t => t.Name == key).DisplayValue);
        Assert.DoesNotContain("test-only-sensitive-value", JsonSerializer.Serialize(review));
    }

    [Fact]
    public void Validate_Should_PreserveEmptyValues_When_OptionalParameterIsDefined()
    {
        var preview = Preview();
        preview.Config.Global["_OptionalFlag"] = "";
        preview.WatchItem!.Events[0].Children.Add(new ActionConfig
        {
            Type = ActionType.RunCommand, Tag = "OptionalArgument", Command = "echo [_OptionalFlag]",
        });
        var review = Service().Validate(preview, Live());
        Assert.True(review.CanCreate);
        Assert.Equal("(empty)", Assert.Single(review.Tokens, t => t.Name == "_OptionalFlag").DisplayValue);
    }

    [Fact]
    public void Validate_Should_CheckOtherInitializeSources_When_RecipeReferencesAnExternalFile()
    {
        var preview = Preview();
        preview.WatchItem!.Events[0].Children.Add(new InitializeConfig
        {
            Tag = "OtherInit", ParameterFile = Path.Combine(_root, "unrelated.json"),
        });
        var review = Service().Validate(preview, Live());
        Assert.False(review.CanCreate);
        Assert.Contains(review.Blocking, b => b.Message.Contains("OtherInit") && b.Message.Contains("does not exist"));
        Assert.Empty(review.Tokens);
    }

    [Fact]
    public void Validate_Should_LoadAdditionalSource_When_RecipeInitializesItBeforeActions()
    {
        var preview = Preview();
        var path = Path.Combine(_root, "additional.txt");
        File.WriteAllText(path, "_Agent1,external-agent");
        preview.WatchItem!.Events[0].Children.Insert(1, new InitializeConfig
        {
            Tag = "OtherInit", ParameterFile = path,
        });

        var review = Service().Validate(preview, Live());

        Assert.True(review.CanCreate);
        Assert.Equal("external-agent", Assert.Single(review.Tokens, t => t.Name == "_Agent1").DisplayValue);
        Assert.False(File.Exists(preview.ConfigPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_Should_RefuseOverwrite_When_OutputAppearsAfterReview(bool configFile)
    {
        var preview = Preview();
        var review = Service().Validate(preview, Live(), _fragments);
        var fragmentPath = Path.Combine(_fragments, $"{preview.Tag}.watchitem.xml");
        Directory.CreateDirectory(_fragments);
        var existing = configFile ? preview.ConfigPath : fragmentPath;
        File.WriteAllText(existing, "keep-existing-content");

        var outcome = await CreateAsAdmin(preview, review);

        Assert.False(outcome.Written);
        Assert.Equal("keep-existing-content", File.ReadAllText(existing));
        Assert.False(File.Exists(configFile ? fragmentPath : preview.ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Create_Should_RefuseStaleReview_When_ProfileChangesAfterValidation()
    {
        var preview = Preview();
        var review = Service().Validate(preview, Live());
        ParameterResolver.CollectInitializeNodes(preview.WatchItem!).Single().Profile = "Missing";

        var outcome = await CreateAsAdmin(preview, review);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("Profile"));
        Assert.False(File.Exists(preview.ConfigPath));
    }

    [Fact]
    public async Task Create_Should_RecheckLiveConflicts_When_PipelineAppearsAfterReview()
    {
        var preview = Preview();
        var review = Service().Validate(preview, Live());
        var live = Live(new WatchItemConfig { Tag = preview.Tag, Path = @"C:\elsewhere" });

        var outcome = await Service().CreateAsync(preview, review, _fragments, BuilderAuth.Admin, live: live);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("already exists"));
        Assert.False(File.Exists(preview.ConfigPath));
    }

    [Fact]
    public async Task Create_Should_RemoveOnlyOwnOutput_When_SecondPublishFails()
    {
        var preview = Preview();
        var review = Service().Validate(preview, Live());
        var occupiedPath = Path.Combine(_fragments, $"{preview.Tag}.watchitem.xml");
        Directory.CreateDirectory(occupiedPath);

        var outcome = await CreateAsAdmin(preview, review);

        Assert.False(outcome.Written);
        Assert.Contains(outcome.Issues, i => i.Severity == BuilderSeverity.Error);
        Assert.False(File.Exists(preview.ConfigPath));
        Assert.True(Directory.Exists(occupiedPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Create_Should_RoundTripThroughImportAndPreflight_When_RuntimeFactsAreSupplied()
    {
        var preview = Preview();
        var review = Service().Validate(preview, Live());
        // Config is the validated source of truth, not a second potentially stale JSON representation.
        preview = preview with { ConfigJson = "{}" };
        var outcome = await CreateAsAdmin(preview, review);
        Assert.True(outcome.Written);
        var (items, templates) = WatchListXmlParser.ParseWatchItemsFromXml(File.ReadAllText(outcome.FragmentPath));
        var live = Live();
        var import = WatchListFragmentImport.Plan(live, items, templates);
        Assert.True(import.CanImport);
        live.WatchItems.AddRange(import.NewItems);
        live.Templates.AddRange(import.NewTemplates);
        Assert.DoesNotContain(WatchListValidator.Analyze(live), i => i.Severity == WatchIssueSeverity.Error);

        var fs = new Mock<IPreflightFileSystem>();
        fs.Setup(f => f.CheckFile(It.IsAny<string>())).Returns(PathAccess.Exists);
        fs.Setup(f => f.CheckDirectory(It.IsAny<string>())).Returns(PathAccess.Exists);
        fs.Setup(f => f.FreeSpaceGb(It.IsAny<string>())).Returns(200);
        fs.SetupGet(f => f.Identity).Returns("test");
        var runner = new PreflightRunner(fs.Object, name => new PreflightAgentFacts
        {
            AgentName = name, IsRegistered = true, IsOnline = true,
            MaintenanceState = "None", DiskFreeGb = 200,
        });
        var pipeline = Assert.Single(live.WatchItems);
        var report = runner.Run(new PreflightRequest
        {
            Config = live, Pipeline = pipeline,
            TriggerValues = new Dictionary<string, string> { ["_BuildNumber"] = "test-build" },
        });
        Assert.Empty(report.Failures);
        Assert.True(Directory.Exists(pipeline.Path));
        Assert.False(File.Exists(Path.Combine(pipeline.Path, pipeline.Filter)));
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
            _audit,
            Path.Combine(_root, "Triggers"));

        var preview = Preview();
        var validation = service.Validate(preview, Live());

        var outcome = await service.CreateAsync(preview, validation, _fragments, BuilderAuth.Engineer);

        Assert.False(outcome.Written);
        Assert.Empty(_audit.Entries);
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
            _audit,
            Path.Combine(_root, "Triggers"));

        var preview = Preview();
        var outcome = await service.CreateAsync(
            preview, service.Validate(preview, Live()), _fragments, BuilderAuth.Admin);

        Assert.True(outcome.Written);
    }

    [Fact]
    public async Task Create_Should_WriteOneAuditEntry_When_FilesAreWritten()
    {
        var preview = Preview();

        var outcome = await CreateAsAdmin(preview, Service().Validate(preview, Live()));

        Assert.True(outcome.Written);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("Pipeline_Author", entry.ActionName);
        Assert.Equal(preview.Tag, entry.ResourceId);
        Assert.Equal(BuilderAuth.Admin.UserId, entry.UserId);
        Assert.True(entry.Allowed);
        Assert.Equal("pipeline-fragment-created", entry.ReasonCode);
    }

    [Fact]
    public async Task Create_Should_NotAudit_When_ValidationBlocksTheWrite()
    {
        var outcome = await CreateAsAdmin(Preview(), new BuilderValidation([new BuilderIssue(BuilderSeverity.Error, "no")], []));

        Assert.False(outcome.Written);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Create_Should_CheckPermissionBeforeValidation_So_ADeniedCallerLearnsNothingElse()
    {
        var service = new PipelineBuilderService(
            new DerivedTargetCatalog(_parameters),
            new FolderRecipeSource(_recipes),
            new FolderStageTemplateSource(_templates),
            BuilderAuth.AdminOnly,
            _audit,
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
