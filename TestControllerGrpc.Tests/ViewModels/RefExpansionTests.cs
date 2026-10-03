using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// A Ref shows the referenced template's nodes inline, resolved against the pipeline that owns the
/// Ref - so the same template previews different values under SP2023R2SP2 and SP2026.
/// </summary>
/// <remarks>
/// The expansion carries the TEMPLATE's model objects. If it were writable, editing a Ref'd action
/// would rewrite the shared template for every pipeline using it - the same class of defect as the
/// `ResolveAgentNamesInConfig` bug that baked resolved agent names into WatchList.xml.
/// </remarks>
[Collection("TokenState")]
public class RefExpansionTests : IDisposable
{
    public RefExpansionTests()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
        TemplateRunContext.ClearAll();
    }

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.ClearSessionValues();
        TemplateRunContext.ClearAll();
        GC.SuppressFinalize(this);
    }

    /// <summary>Two pipelines Ref the SAME template, with different values for [_Installer].</summary>
    private static WatchListConfig TwoPipelinesSharingATemplate() => new()
    {
        WatchItems =
        [
            Pipeline("SP2023R2SP2"),
            Pipeline("SP2026"),
        ],
        Templates =
        [
            new TemplateConfig
            {
                ID = "InstallSanity_Single",
                Children =
                [
                    new ActionConfig
                    {
                        Tag = "Install on jvgr1",
                        Type = ActionType.RunRemoteCommand,
                        AgentName = "[_Agent1]",
                        Command = "[_Installer]",
                        Parameters = "[_DropLocation] 30",
                    },
                ],
            },
        ],
    };

    private static WatchItemConfig Pipeline(string tag) => new()
    {
        Tag = tag,
        Path = @"C:\Triggers",
        Events =
        [
            new EventConfig
            {
                Type = "Renamed",
                Children = [new RefConfig { TemplateID = "InstallSanity_Single" }],
            },
        ],
    };

    private static TreeNodeViewModel RefUnder(TreeNodeViewModel root, string pipelineTag)
        => root.Children.Single(c => c.Tag == pipelineTag).Children[0].Children[0];

    // ── expansion ───────────────────────────────────────────────────────────

    [Fact]
    public void Ref_Should_ShowTheTemplatesNodes_When_TheTreeIsBuilt()
    {
        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate());

        var refNode = RefUnder(root, "SP2023R2SP2");

        Assert.Equal(NodeKinds.Ref, refNode.NodeKind);
        Assert.Single(refNode.Children);
        Assert.Equal("Install on jvgr1", refNode.Children[0].Tag);
    }

    [Fact]
    public void Ref_Should_StayALeaf_When_TheTemplateIsNotLoaded()
    {
        var config = TwoPipelinesSharingATemplate();
        config.Templates.Clear();

        var refNode = RefUnder(TreeNodeViewModel.FromWatchList(config), "SP2023R2SP2");

        Assert.Empty(refNode.Children);
    }

    [Fact]
    public void Ref_Should_NotRecurseForever_When_ATemplateRefsItself()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("P1")],
            Templates =
            [
                new TemplateConfig
                {
                    ID = "InstallSanity_Single",
                    Children = [new RefConfig { TemplateID = "InstallSanity_Single" }],
                },
            ],
        };

        var refNode = RefUnder(TreeNodeViewModel.FromWatchList(config), "P1");

        // The nested self-Ref is shown, but not expanded again.
        Assert.Single(refNode.Children);
        Assert.Equal(NodeKinds.Ref, refNode.Children[0].NodeKind);
        Assert.Empty(refNode.Children[0].Children);
    }

    [Fact]
    public void Ref_Should_ExpandNestedTemplates_When_OneTemplateRefsAnother()
    {
        var config = new WatchListConfig
        {
            WatchItems = [Pipeline("P1")],
            Templates =
            [
                new TemplateConfig
                {
                    ID = "InstallSanity_Single",
                    Children = [new RefConfig { TemplateID = "Inner" }],
                },
                new TemplateConfig
                {
                    ID = "Inner",
                    Children = [new ActionConfig { Tag = "Deep", Command = "deep.bat" }],
                },
            ],
        };

        var refNode = RefUnder(TreeNodeViewModel.FromWatchList(config), "P1");

        Assert.Equal("Deep", refNode.Children[0].Children[0].Tag);
    }

    // ── resolution per owning pipeline ──────────────────────────────────────

    [Fact]
    public void RefChildren_Should_ResolvePerOwningPipeline_When_TwoPipelinesShareATemplate()
    {
        TreeNodeViewModel.TokensFor("SP2023R2SP2")["_Installer"] = @"C:\TestSetup\SP2023R2SP2\Base\Install-Build.bat";
        TreeNodeViewModel.TokensFor("SP2026")["_Installer"] = @"C:\TestSetup\SP2026\Base\Install-Build.bat";

        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate());

        var a = RefUnder(root, "SP2023R2SP2").Children[0];
        var b = RefUnder(root, "SP2026").Children[0];

        Assert.Equal(@"C:\TestSetup\SP2023R2SP2\Base\Install-Build.bat", a.ResolvedCommand);
        Assert.Equal(@"C:\TestSetup\SP2026\Base\Install-Build.bat", b.ResolvedCommand);
        Assert.NotEqual(a.ResolvedCommand, b.ResolvedCommand);
    }

    [Fact]
    public void RefChild_TokenScope_Should_BeTheOwningPipeline_NotTheTemplate()
    {
        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate());

        Assert.Equal("SP2026", RefUnder(root, "SP2026").Children[0].TokenScope);
    }

    [Fact]
    public void RefChildren_Should_ResolveParametersPerOwningPipeline()
    {
        TreeNodeViewModel.TokensFor("SP2023R2SP2")["_DropLocation"] = @"\\build\SP2023R2SP2";
        TreeNodeViewModel.TokensFor("SP2026")["_DropLocation"] = @"\\build\SP2026";

        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate());

        Assert.Equal(@"\\build\SP2023R2SP2 30", RefUnder(root, "SP2023R2SP2").Children[0].ResolvedParameters);
        Assert.Equal(@"\\build\SP2026 30", RefUnder(root, "SP2026").Children[0].ResolvedParameters);
    }

    // ── read-only ───────────────────────────────────────────────────────────

    [Fact]
    public void RefChildren_Should_BeMarkedAsAnExpansion_SoTheUiCanRefuseEdits()
    {
        var refNode = RefUnder(TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate()), "SP2023R2SP2");

        Assert.False(refNode.IsRefExpansion);          // the Ref itself is a real pipeline node
        Assert.True(refNode.Children[0].IsRefExpansion);
    }

    [Fact]
    public void RefChild_ApplyToModel_Should_LeaveTheTemplateAlone_So_OtherPipelinesAreNotRewritten()
    {
        var config = TwoPipelinesSharingATemplate();
        var templateAction = (ActionConfig)config.Templates[0].Children[0];
        var root = TreeNodeViewModel.FromWatchList(config);

        var child = RefUnder(root, "SP2023R2SP2").Children[0];
        child.Command = "hijacked.bat";
        child.AgentName = "hijacked-agent";
        child.ApplyToModel();

        Assert.Equal("[_Installer]", templateAction.Command);
        Assert.Equal("[_Agent1]", templateAction.AgentName);
    }

    [Fact]
    public void RefExpansion_Should_NotAddNodesToTheModel_So_TheSavedFileIsUnchanged()
    {
        var config = TwoPipelinesSharingATemplate();
        var ev = config.WatchItems[0].Events[0];

        TreeNodeViewModel.FromWatchList(config);

        // The Ref is still one child; the expansion lives only in the view model.
        Assert.Single(ev.Children);
        Assert.IsType<RefConfig>(ev.Children[0]);
    }

    [Fact]
    public void RefExpansion_Should_BeCollapsedInitially_So_ThePipelineShapeStaysReadable()
    {
        var refNode = RefUnder(TreeNodeViewModel.FromWatchList(TwoPipelinesSharingATemplate()), "SP2023R2SP2");

        Assert.False(refNode.IsExpanded);
    }
}
