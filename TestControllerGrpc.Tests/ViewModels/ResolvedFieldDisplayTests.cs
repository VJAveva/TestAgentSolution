using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Unresolved fields used to render in the same green as real values, so "[_Installer]" read as a
/// resolved path. Green now means a value; anything else says so in words.
/// </summary>
[Collection("TokenState")]
public class ResolvedFieldDisplayTests : IDisposable
{
    public ResolvedFieldDisplayTests()
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

    private static WatchListConfig OnePipeline(string tag = "Sanity") => new()
    {
        WatchItems =
        [
            new WatchItemConfig
            {
                Tag = tag,
                Path = @"C:\Triggers",
                Events =
                [
                    new EventConfig
                    {
                        Type = "Renamed",
                        Children =
                        [
                            new ActionConfig
                            {
                                Tag = "Install",
                                AgentName = "[_Agent1]",
                                Command = "[_Installer]",
                                Parameters = "[_DropLocation] 30",
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static TreeNodeViewModel ActionIn(WatchListConfig config, string tag = "Sanity")
        => TreeNodeViewModel.FromWatchList(config).Children.Single(c => c.Tag == tag).Children[0].Children[0];

    // ── unresolved ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[Sequential] RevertingAgents")]
    [InlineData("[Parallel] RevertingAgents")]
    public void Display_Should_LeaveExecutionModeDecorationAlone_When_ItLooksLikeAToken(string label)
    {
        // Marking "[Sequential]" as a token made the label converter strip the brackets and
        // render a bare "(not set) RevertingAgents" for every group in the tree.
        var group = TreeNodeViewModel.FromWatchList(OnePipeline())
            .Children.Single(c => c.Tag == "Sanity").Children[0];

        group.DisplayText = label;

        Assert.Equal(label, group.ResolvedDisplayText);
        Assert.False(group.HasUnresolvedTokens);
        Assert.DoesNotContain("(not set)", group.ResolvedDisplayText);
    }

    [Fact]
    public void Display_Should_StillMarkRealTokens_When_TheLabelAlsoCarriesExecutionMode()
    {
        var group = TreeNodeViewModel.FromWatchList(OnePipeline())
            .Children.Single(c => c.Tag == "Sanity").Children[0];

        group.DisplayText = "[Sequential] Install [_Installer]";

        Assert.Equal("[Sequential] Install [_Installer] (not set)", group.ResolvedDisplayText);
        Assert.True(group.HasUnresolvedTokens);
    }

    [Fact]
    public void Display_Should_MarkTheTokenNotSet_When_ItIsMissingFromAPipelineThatHasAContext()
    {
        var node = ActionIn(OnePipeline());

        // In scope, but no value: name the specific token rather than blanking the whole field.
        Assert.False(node.IsCommandResolved);
        Assert.Equal("[_Installer] (not set)", node.ResolvedCommandDisplay);
    }

    [Fact]
    public void Display_Should_KeepTheWholeFieldHint_When_TheNodeHasNoPipelineContextAtAll()
    {
        // A Library node whose template has no chosen pipeline can resolve nothing, so marking each
        // token "(not set)" would blame the data instead of the missing context.
        var templates = new List<TemplateConfig>
        {
            new()
            {
                ID = "OrphanTemplate",
                Children = [new ActionConfig { Command = "[_Installer]", Type = ActionType.RunRemoteCommand }],
            },
        };

        var action = TreeNodeViewModel.FromTemplateList(templates).Children[0].Children[0];

        Assert.Null(action.TokenScope);
        Assert.True(action.HasNoTokenContext);
        Assert.Equal(TreeNodeViewModel.UnresolvedHint, action.ResolvedCommandDisplay);
    }

    [Fact]
    public void Display_Should_NeverRenderAnUnresolvedTokenAsAValue()
    {
        var node = ActionIn(OnePipeline());

        // The token stays visible so the user knows WHICH one is missing - but it is always
        // marked, never left looking like a resolved value.
        Assert.Contains("(not set)", node.ResolvedCommandDisplay);
        Assert.Contains("(not set)", node.ResolvedParametersDisplay);
        Assert.Contains("(not set)", node.ResolvedAgentNameDisplay);
        Assert.False(node.IsCommandResolved);
        Assert.False(node.IsAgentNameResolved);
    }

    [Fact]
    public void Display_Should_FlagAPartialResolution_When_OnlySomeTokensResolve()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_DropLocation"] = @"\\build\drop";
        var node = ActionIn(OnePipeline());

        // "[_DropLocation] 30" resolves, but Command still has [_Installer].
        Assert.True(node.IsParametersResolved);
        Assert.False(node.IsCommandResolved);
    }

    [Fact]
    public void Display_Should_TreatABlankFieldAsResolved_So_EmptyBoxesAreNotScolded()
    {
        var config = OnePipeline();
        var action = (ActionConfig)config.WatchItems[0].Events[0].Children[0];
        action.Command = "";
        action.AgentName = "";

        var node = ActionIn(config);

        Assert.True(node.IsCommandResolved);
        Assert.Equal("", node.ResolvedCommandDisplay);
        Assert.True(node.IsAgentNameResolved);
    }

    // ── resolved ────────────────────────────────────────────────────────────

    [Fact]
    public void Display_Should_ShowTheValue_When_EveryTokenResolves()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Installer"] = @"C:\Base\Install-Build.bat";
        TreeNodeViewModel.TokensFor("Sanity")["_DropLocation"] = @"\\build\drop";
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";

        var node = ActionIn(OnePipeline());

        Assert.True(node.IsCommandResolved);
        Assert.Equal(@"C:\Base\Install-Build.bat", node.ResolvedCommandDisplay);
        Assert.True(node.IsAgentNameResolved);
        Assert.Equal("jvgr1", node.ResolvedAgentNameDisplay);
    }

    [Fact]
    public void Display_Should_Refresh_When_TheCommandIsEdited()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Installer"] = @"C:\Base\Install-Build.bat";
        var node = ActionIn(OnePipeline());
        Assert.True(node.IsCommandResolved);

        node.Command = "[_Missing]";

        Assert.False(node.IsCommandResolved);
        Assert.Equal("[_Missing] (not set)", node.ResolvedCommandDisplay);
    }

    [Fact]
    public void Display_Should_Refresh_When_TokensArriveAfterTheTreeWasBuilt()
    {
        var root = TreeNodeViewModel.FromWatchList(OnePipeline());
        var node = root.Children[0].Children[0].Children[0];
        Assert.False(node.IsCommandResolved);

        TreeNodeViewModel.TokensFor("Sanity")["_Installer"] = @"C:\Base\Install-Build.bat";
        root.RefreshResolvedTextRecursive();

        Assert.True(node.IsCommandResolved);
        Assert.Equal(@"C:\Base\Install-Build.bat", node.ResolvedCommandDisplay);
    }

    [Fact]
    public void Display_Should_RaiseChangeNotifications_So_ThePanelDoesNotGoStale()
    {
        var node = ActionIn(OnePipeline());
        var seen = new List<string>();
        node.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        node.Command = "literal.bat";

        Assert.Contains(nameof(TreeNodeViewModel.IsCommandResolved), seen);
        Assert.Contains(nameof(TreeNodeViewModel.ResolvedCommandDisplay), seen);
    }

    // ── session values ──────────────────────────────────────────────────────

    [Fact]
    public void SessionValues_Should_OutrankThePreviewDictionary_When_ARunIsKnown()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Installer"] = @"C:\Predicted\Install.bat";
        TreeNodeViewModel.SetSessionValues("Sanity", "d91937",
            new Dictionary<string, string> { ["_Installer"] = @"C:\Actually\Used.bat" });

        var node = ActionIn(OnePipeline());

        Assert.Equal(@"C:\Actually\Used.bat", node.ResolvedCommand);
        Assert.Equal("Values used in run d91937", node.ValuesSourceLabel);
        Assert.True(node.HasSessionValues);
    }

    [Fact]
    public void SessionValues_Should_ApplyOnlyToTheirOwnPipeline()
    {
        TreeNodeViewModel.TokensFor("Warm")["_Installer"] = @"C:\Predicted\Warm.bat";
        TreeNodeViewModel.SetSessionValues("Sanity", "d91937",
            new Dictionary<string, string> { ["_Installer"] = @"C:\Actually\Used.bat" });

        var warm = ActionIn(OnePipeline("Warm"), "Warm");

        Assert.Equal(@"C:\Predicted\Warm.bat", warm.ResolvedCommand);
        Assert.False(warm.HasSessionValues);
        Assert.Equal("", warm.ValuesSourceLabel);
    }

    [Fact]
    public void SessionValues_Should_Clear_So_AStaleRunDoesNotLingerAfterReload()
    {
        TreeNodeViewModel.SetSessionValues("Sanity", "d91937",
            new Dictionary<string, string> { ["_Installer"] = @"C:\Actually\Used.bat" });

        TreeNodeViewModel.ClearSessionValues();

        Assert.False(ActionIn(OnePipeline()).HasSessionValues);
    }

    [Fact]
    public void SessionValues_Should_ReachRefChildren_So_ATemplatesNodesShowWhatRan()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = "Sanity",
                    Path = @"C:\Triggers",
                    Events = [new EventConfig { Type = "Renamed", Children = [new RefConfig { TemplateID = "T1" }] }],
                },
            ],
            Templates =
            [
                new TemplateConfig
                {
                    ID = "T1",
                    Children = [new ActionConfig { Tag = "Install", Command = "[_Installer]" }],
                },
            ],
        };

        TreeNodeViewModel.SetSessionValues("Sanity", "d91937",
            new Dictionary<string, string> { ["_Installer"] = @"C:\Actually\Used.bat" });

        var child = TreeNodeViewModel.FromWatchList(config).Children[0].Children[0].Children[0].Children[0];

        Assert.Equal(@"C:\Actually\Used.bat", child.ResolvedCommand);
        Assert.Equal("Values used in run d91937", child.ValuesSourceLabel);
    }
}
