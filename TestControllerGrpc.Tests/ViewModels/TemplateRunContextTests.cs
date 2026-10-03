using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// A template has no settings of its own. Pointing it at a pipeline for the session must resolve
/// the WHOLE subtree against that pipeline - and only against a pipeline that actually Refs it.
/// </summary>
[Collection("TokenState")]
public class TemplateRunContextTests : IDisposable
{
    public TemplateRunContextTests()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TemplateRunContext.ClearAll();
    }

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        TemplateRunContext.ClearAll();
        GC.SuppressFinalize(this);
    }

    private static WatchListConfig Config() => new()
    {
        WatchItems =
        [
            new WatchItemConfig
            {
                Tag = "Sanity",
                Events =
                [
                    new EventConfig
                    {
                        Children = [new RefConfig { TemplateID = "Prep" }],
                    },
                ],
            },
            new WatchItemConfig
            {
                Tag = "Warm",
                Events =
                [
                    new EventConfig
                    {
                        Children = [new RefConfig { TemplateID = "Prep" }],
                    },
                ],
            },
        ],
        Templates =
        [
            new TemplateConfig
            {
                ID = "Prep",
                Children =
                [
                    new ActionGroupConfig
                    {
                        Tag = "Copy",
                        Children =
                        [
                            new ActionConfig
                            {
                                Tag = "Copy build",
                                Command = @"\\[_Agent1]\C$\copy.bat",
                                Parameters = "-build [_BuildNumber]",
                            },
                        ],
                    },
                    new ActionConfig { Tag = "Run", Command = "[_Agent1]-run.bat" },
                ],
            },
            new TemplateConfig
            {
                ID = "Smoke",
                Children = [new ActionConfig { Tag = "Smoke", Command = "[_Agent1]-smoke.bat" }],
            },
        ],
    };

    private static List<TreeNodeViewModel> Flatten(TreeNodeViewModel root)
    {
        var all = new List<TreeNodeViewModel> { root };
        foreach (var c in root.Children) all.AddRange(Flatten(c));
        return all;
    }

    private static void SeedTokens()
    {
        TreeNodeViewModel.TokensFor("Sanity")["_Agent1"] = "jvgr1";
        TreeNodeViewModel.TokensFor("Warm")["_Agent1"] = "warmgr7";
        TreeNodeViewModel.SharedTokens["_BuildNumber"] = "2026.1";
    }

    [Fact]
    public void TokenScope_Should_BeNull_When_TemplateHasNoContext()
    {
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);

        foreach (var node in Flatten(root.Children[0]))
            Assert.Null(node.TokenScope);
    }

    [Fact]
    public void TokenScope_Should_BeTheContextPipeline_ForEveryNodeInTheSubtree()
    {
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);

        TemplateRunContext.Set("Prep", "Warm");

        foreach (var node in Flatten(root.Children[0]))
            Assert.Equal("Warm", node.TokenScope);
    }

    /// <summary>Setting the context from a leaf must change its siblings and its parent too.</summary>
    [Fact]
    public void Context_Should_ApplyToWholeTemplate_When_SetFromADeepChild()
    {
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);
        var template = root.Children[0];
        var deepChild = Flatten(template).Last(n => n.NodeKind == NodeKinds.Action);

        TemplateRunContext.Set(deepChild.OwningTemplateId, "Sanity");

        Assert.Equal("Sanity", template.TokenScope);
        foreach (var node in Flatten(template))
            Assert.Equal("Sanity", node.TokenScope);
    }

    [Fact]
    public void ResolvedCommand_Should_UseTheContextPipeline()
    {
        SeedTokens();
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);
        var action = Flatten(root.Children[0]).First(n => n.Tag == "Copy build");

        Assert.Equal(@"\\[_Agent1]\C$\copy.bat", action.ResolvedCommand);

        TemplateRunContext.Set("Prep", "Sanity");
        root.RefreshResolvedTextRecursive();
        Assert.Equal(@"\\jvgr1\C$\copy.bat", action.ResolvedCommand);

        TemplateRunContext.Set("Prep", "Warm");
        root.RefreshResolvedTextRecursive();
        Assert.Equal(@"\\warmgr7\C$\copy.bat", action.ResolvedCommand);
    }

    [Fact]
    public void ResolvedParameters_Should_FallBackToSharedTokens_When_ContextIsSet()
    {
        SeedTokens();
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);
        var action = Flatten(root.Children[0]).First(n => n.Tag == "Copy build");

        TemplateRunContext.Set("Prep", "Sanity");
        root.RefreshResolvedTextRecursive();

        Assert.Equal("-build 2026.1", action.ResolvedParameters);
    }

    [Fact]
    public void Context_Should_BePerTemplate_When_TwoTemplatesAreUsed()
    {
        SeedTokens();
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);

        TemplateRunContext.Set("Prep", "Sanity");
        TemplateRunContext.Set("Smoke", "Warm");
        root.RefreshResolvedTextRecursive();

        var prepRun = Flatten(root.Children[0]).First(n => n.NodeKind == NodeKinds.Action && n.Tag == "Run");
        var smoke = Flatten(root.Children[1]).First(n => n.NodeKind == NodeKinds.Action && n.Tag == "Smoke");

        Assert.Equal("jvgr1-run.bat", prepRun.ResolvedCommand);
        Assert.Equal("warmgr7-smoke.bat", smoke.ResolvedCommand);
    }

    [Fact]
    public void Context_Should_NotLeakIntoTheWatchListTree()
    {
        SeedTokens();
        var cfg = Config();
        TemplateRunContext.Set("Prep", "Warm");

        var watchRoot = TreeNodeViewModel.FromWatchList(cfg);
        foreach (var node in Flatten(watchRoot).Where(n => n.NodeKind == NodeKinds.WatchItem))
            Assert.Equal(node.Tag, node.TokenScope);
    }

    [Fact]
    public void Context_Should_BeGone_When_Cleared()
    {
        SeedTokens();
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);
        var action = Flatten(root.Children[0]).First(n => n.NodeKind == NodeKinds.Action && n.Tag == "Run");

        TemplateRunContext.Set("Prep", "Sanity");
        root.RefreshResolvedTextRecursive();
        Assert.Equal("jvgr1-run.bat", action.ResolvedCommand);

        TemplateRunContext.Clear("Prep");
        root.RefreshResolvedTextRecursive();
        Assert.Equal("[_Agent1]-run.bat", action.ResolvedCommand);
        Assert.Null(action.TokenScope);
    }

    [Fact]
    public void ClearAll_Should_DropEveryContext_When_TheWatchListReloads()
    {
        TemplateRunContext.Set("Prep", "Sanity");
        TemplateRunContext.Set("Smoke", "Warm");

        TemplateRunContext.ClearAll();

        Assert.Null(TemplateRunContext.For("Prep"));
        Assert.Null(TemplateRunContext.For("Smoke"));
    }

    [Fact]
    public void TemplateContextLabel_Should_NameThePipeline_OnTheTemplateHeaderOnly()
    {
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);
        var template = root.Children[0];
        var child = Flatten(template).First(n => n.NodeKind == NodeKinds.Action && n.Tag == "Run");

        Assert.Equal("", template.TemplateContextLabel);
        Assert.False(template.HasTemplateContext);

        TemplateRunContext.Set("Prep", "Sanity");

        Assert.Equal("Context: Sanity", template.TemplateContextLabel);
        Assert.True(template.HasTemplateContext);
        Assert.Equal("", child.TemplateContextLabel); // chip belongs on the header, not every row
        Assert.False(child.HasTemplateContext);
    }

    [Fact]
    public void Changed_Should_Fire_When_ContextIsSetOrCleared()
    {
        var fired = new List<string?>();
        void Handler(string? id) => fired.Add(id);
        TemplateRunContext.Changed += Handler;
        try
        {
            TemplateRunContext.Set("Prep", "Sanity");
            TemplateRunContext.Set("Prep", "Sanity"); // no change, no event
            TemplateRunContext.Set("Prep", "Warm");
            TemplateRunContext.Clear("Prep");
            TemplateRunContext.Clear("Prep");         // already gone, no event
            TemplateRunContext.Set("Smoke", "Warm");
            TemplateRunContext.ClearAll();
        }
        finally
        {
            TemplateRunContext.Changed -= Handler;
        }

        Assert.Equal(["Prep", "Prep", "Prep", "Smoke", null], fired);
    }

    [Fact]
    public void OwningTemplateId_Should_BeNull_ForWatchListNodes()
    {
        var watchRoot = TreeNodeViewModel.FromWatchList(Config());

        foreach (var node in Flatten(watchRoot))
            Assert.Null(node.OwningTemplateId);
    }

    [Fact]
    public void OwningTemplateId_Should_BeTheTemplate_ForEveryNodeUnderIt()
    {
        var root = TreeNodeViewModel.FromTemplateList(Config().Templates);

        foreach (var node in Flatten(root.Children[0]))
            Assert.Equal("Prep", node.OwningTemplateId);
        foreach (var node in Flatten(root.Children[1]))
            Assert.Equal("Smoke", node.OwningTemplateId);
    }
}
