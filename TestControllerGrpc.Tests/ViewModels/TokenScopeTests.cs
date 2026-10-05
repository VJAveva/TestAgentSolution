using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Preview values are per pipeline. One flat dictionary used to serve every pipeline, so the last
/// WatchItem loaded won each key - and a Templates-library node previewed fully resolved values it
/// would never actually receive, while the run failed on those very tokens.
/// </summary>
[Collection("TokenState")]
public class TokenScopeTests : IDisposable
{
    public TokenScopeTests() => TreeNodeViewModel.ClearTokenScopes();

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        GC.SuppressFinalize(this);
    }

    private static WatchListConfig TwoPipelinesAndATemplate() => new()
    {
        WatchItems =
        [
            new WatchItemConfig
            {
                Tag = "Sanity",
                Path = @"C:\Triggers",
                Events =
                [
                    new EventConfig
                    {
                        Type = "Renamed",
                        Children = [new ActionConfig { Tag = "Run", AgentName = "[_Agent1]", Command = "run.bat" }],
                    },
                ],
            },
            new WatchItemConfig
            {
                Tag = "Warm",
                Path = @"C:\Triggers",
                Events =
                [
                    new EventConfig
                    {
                        Type = "Renamed",
                        Children = [new ActionConfig { Tag = "Run", AgentName = "[_Agent1]", Command = "run.bat" }],
                    },
                ],
            },
        ],
        Templates =
        [
            new TemplateConfig
            {
                ID = "PrepSanity",
                Children = [new ActionConfig { Tag = "Prep", AgentName = "[_Agent1]", Command = "prep.bat" }],
            },
        ],
    };

    private static TreeNodeViewModel ActionUnder(TreeNodeViewModel root, string pipelineTag)
    {
        var wi = root.Children.Single(c => c.Tag == pipelineTag);
        return wi.Children[0].Children[0];
    }

    [Fact]
    public void TokenScope_Should_BeTheOwningPipeline_When_NodeIsInTheWatchListTree()
    {
        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesAndATemplate());

        Assert.Equal("Sanity", ActionUnder(root, "Sanity").TokenScope);
        Assert.Equal("Warm", ActionUnder(root, "Warm").TokenScope);
    }

    [Fact]
    public void TokenScope_Should_BeNull_When_NodeIsInTheTemplatesLibrary()
    {
        var config = TwoPipelinesAndATemplate();
        var root = TreeNodeViewModel.FromTemplateList(config.Templates);
        var action = root.Children[0].Children[0];

        Assert.Null(action.TokenScope);
    }

    /// <summary>
    /// The exact confusion from the bug report: two pipelines define _Agent1 differently, and the
    /// tree must show each its own value instead of whichever loaded last.
    /// </summary>
    [Fact]
    public void ResolveTokens_Should_UseEachPipelinesOwnValue_When_TwoPipelinesDefineTheSameKey()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";
        TreeNodeViewModel.TokensFor("Warm")["_Agent1"] = "warmgr";

        var root = TreeNodeViewModel.FromWatchList(TwoPipelinesAndATemplate());

        Assert.Equal("jvgr1", TreeNodeViewModel.ResolveTokens("[_Agent1]", ActionUnder(root, "Sanity").TokenScope));
        Assert.Equal("warmgr", TreeNodeViewModel.ResolveTokens("[_Agent1]", ActionUnder(root, "Warm").TokenScope));
    }

    /// <summary>
    /// A library template must preview UNRESOLVED. Showing a value there is a guess at which
    /// pipeline will run it, and it is exactly what made a failing run look like it should succeed.
    /// </summary>
    [Fact]
    public void ResolveTokens_Should_LeaveTokensUnresolved_When_NodeIsALibraryTemplate()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";
        TreeNodeViewModel.SharedTokens["_ControllerName"] = "jvgr22";

        var config = TwoPipelinesAndATemplate();
        var root = TreeNodeViewModel.FromTemplateList(config.Templates);
        var action = root.Children[0].Children[0];

        Assert.Equal("[_Agent1]", TreeNodeViewModel.ResolveTokens("[_Agent1]", action.TokenScope));
        Assert.Equal("[_ControllerName]", TreeNodeViewModel.ResolveTokens("[_ControllerName]", action.TokenScope));
    }

    [Fact]
    public void ResolveTokens_Should_FallBackToShared_When_ThePipelineDoesNotDefineTheKey()
    {
        TreeNodeViewModel.SharedTokens["_ControllerName"] = "jvgr22";
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";

        Assert.Equal("jvgr22", TreeNodeViewModel.ResolveTokens("[_ControllerName]", "Sanity"));
    }

    [Fact]
    public void ResolveTokens_Should_PreferThePipelineValue_When_SharedAlsoDefinesTheKey()
    {
        TreeNodeViewModel.SharedTokens["_Agent1"] = "global-agent";
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";

        Assert.Equal("jvgr1", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Sanity"));
    }

    [Fact]
    public void ResolveTokens_Should_ResolveBothAliases_When_KeyIsStoredWithoutUnderscore()
    {
        TreeNodeViewModel.TokensFor("Sanity")["BuildNumber"] = "1.2.3";

        Assert.Equal("1.2.3", TreeNodeViewModel.ResolveTokens("[BuildNumber]", "Sanity"));
        Assert.Equal("1.2.3", TreeNodeViewModel.ResolveTokens("[_BuildNumber]", "Sanity"));
    }

    [Fact]
    public void ResolveTokens_Should_MarkUnknownTokens_When_NoLayerDefinesThem()
    {
        // Still visible, so the user can see WHICH token is missing - but marked, never bare.
        Assert.Equal("[_Nope] (not set)", TreeNodeViewModel.ResolveTokens("[_Nope]", "Sanity"));
    }

    [Fact]
    public void ResolvedDisplayText_Should_StayTokenised_When_NodeIsALibraryTemplate()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";

        var config = TwoPipelinesAndATemplate();
        var root = TreeNodeViewModel.FromTemplateList(config.Templates);
        root.RefreshResolvedTextRecursive();

        var action = root.Children[0].Children[0];
        Assert.Equal("[_Agent1]", action.ResolvedAgentName);
        Assert.True(action.HasUnresolvedTokens || !action.ResolvedDisplayText.Contains("jvgr1"));
    }
}
