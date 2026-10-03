using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Selecting a node inside a template must always make THAT node the one the properties pane shows
/// and the one Execute acts on.
///
/// Two separate faults broke this and both looked intermittent: MainViewModel switched context only
/// inside the [ObservableProperty] change callbacks (which do not fire on a repeat selection), and
/// WPF does not select a TreeViewItem on right-click. The visible symptom was the worst kind - a
/// template context menu that executed a WatchList node.
/// </summary>
public class TreeSelectionCoordinatorTests
{
    private static WatchListConfig Config() => new()
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
                        Children =
                        [
                            new InitializeConfig { Tag = "Init", ParameterFile = @"C:\p\cfg.json" },
                            new ActionGroupConfig
                            {
                                Tag = "Install",
                                Children = [new ActionConfig { Tag = "Run", Command = "run.bat" }],
                            },
                        ],
                    },
                ],
            },
        ],
        Templates =
        [
            new TemplateConfig
            {
                ID = "PrepSanity",
                Children =
                [
                    new ActionGroupConfig
                    {
                        Tag = "Prepare Agents",
                        Children =
                        [
                            new ActionGroupConfig
                            {
                                Tag = "Copy files",
                                Children =
                                [
                                    new ActionConfig { Tag = "Copy jvgr1", Command = "copy1.bat" },
                                    new ActionConfig { Tag = "Copy jvgr2", Command = "copy2.bat" },
                                ],
                            },
                            new ActionConfig { Tag = "Run Prepare", Command = "prep.bat" },
                        ],
                    },
                ],
            },
            new TemplateConfig
            {
                ID = "PrepWarm",
                Children = [new ActionConfig { Tag = "Warm prep", Command = "warm.bat" }],
            },
        ],
    };

    private static List<TreeNodeViewModel> Flatten(TreeNodeViewModel root)
    {
        var all = new List<TreeNodeViewModel> { root };
        foreach (var c in root.Children) all.AddRange(Flatten(c));
        return all;
    }

    [Fact]
    public void Coordinator_Should_TrackEveryTemplateNode_When_SelectedInSequence()
    {
        var cfg = Config();
        var templates = TreeNodeViewModel.FromTemplateList(cfg.Templates);
        var nodes = Flatten(templates);
        Assert.True(nodes.Count >= 7, $"expected a deep template tree, got {nodes.Count} nodes");

        var sut = new TreeSelectionCoordinator();

        foreach (var node in nodes)
        {
            sut.ActivateTemplate(node);

            Assert.Same(node, sut.ActiveNode);
            Assert.Same(node, sut.ExecTarget);
            Assert.Equal(TreeSelectionCoordinator.TemplatesContext, sut.ActiveContext);
        }
    }

    /// <summary>
    /// The exact sequence that broke: template node -> WatchList node -> the SAME template node.
    /// The third step is a repeat selection, so a change-notification-driven design never fires.
    /// </summary>
    [Fact]
    public void Coordinator_Should_ReactivateTemplates_When_SameNodeIsReselectedAfterWatchList()
    {
        var cfg = Config();
        var templateNode = Flatten(TreeNodeViewModel.FromTemplateList(cfg.Templates)).Last();
        var watchNode = Flatten(TreeNodeViewModel.FromWatchList(cfg)).Last();

        var sut = new TreeSelectionCoordinator();

        sut.ActivateTemplate(templateNode);
        Assert.Same(templateNode, sut.ExecTarget);

        sut.ActivateWatchList(watchNode);
        Assert.Same(watchNode, sut.ExecTarget);
        Assert.Equal(TreeSelectionCoordinator.WatchListContext, sut.ActiveContext);

        // Re-select the SAME template node.
        sut.ActivateTemplate(templateNode);

        Assert.Same(templateNode, sut.ActiveNode);
        Assert.Same(templateNode, sut.ExecTarget);
        Assert.Equal(TreeSelectionCoordinator.TemplatesContext, sut.ActiveContext);
    }

    /// <summary>
    /// Interleaving the two trees node by node: the properties pane and the run target must agree
    /// after every single step, never lag one behind.
    /// </summary>
    [Fact]
    public void Coordinator_Should_KeepPaneAndExecTargetInStep_When_TreesAreInterleaved()
    {
        var cfg = Config();
        var templateNodes = Flatten(TreeNodeViewModel.FromTemplateList(cfg.Templates));
        var watchNodes = Flatten(TreeNodeViewModel.FromWatchList(cfg));

        var sut = new TreeSelectionCoordinator();

        for (var i = 0; i < templateNodes.Count; i++)
        {
            var t = templateNodes[i];
            sut.ActivateTemplate(t);
            Assert.Same(t, sut.ActiveNode);
            Assert.Same(t, sut.ExecTarget);

            var w = watchNodes[i % watchNodes.Count];
            sut.ActivateWatchList(w);
            Assert.Same(w, sut.ActiveNode);
            Assert.Same(w, sut.ExecTarget);
        }
    }

    [Fact]
    public void Coordinator_Should_KeepEachTreesOwnNode_When_SwitchingBackAndForth()
    {
        var cfg = Config();
        var t = Flatten(TreeNodeViewModel.FromTemplateList(cfg.Templates))[2];
        var w = Flatten(TreeNodeViewModel.FromWatchList(cfg))[2];

        var sut = new TreeSelectionCoordinator();
        sut.ActivateTemplate(t);
        sut.ActivateWatchList(w);

        Assert.Same(t, sut.TemplateNode);
        Assert.Same(w, sut.WatchListNode);
    }

    [Fact]
    public void Coordinator_Should_IgnoreNull_When_SelectionIsCleared()
    {
        var cfg = Config();
        var t = Flatten(TreeNodeViewModel.FromTemplateList(cfg.Templates))[1];

        var sut = new TreeSelectionCoordinator();
        sut.ActivateTemplate(t);
        sut.ActivateTemplate(null);

        Assert.Same(t, sut.ActiveNode);
        Assert.Same(t, sut.ExecTarget);
    }

    [Fact]
    public void Coordinator_Should_ForgetEverything_When_Reset()
    {
        var cfg = Config();
        var sut = new TreeSelectionCoordinator();
        sut.ActivateTemplate(Flatten(TreeNodeViewModel.FromTemplateList(cfg.Templates))[1]);

        sut.Reset();

        Assert.Null(sut.ActiveNode);
        Assert.Null(sut.ExecTarget);
        Assert.Equal(TreeSelectionCoordinator.WatchListContext, sut.ActiveContext);
    }

    /// <summary>
    /// Nodes from two different templates must never be confused: each activation replaces the
    /// template target outright.
    /// </summary>
    [Fact]
    public void Coordinator_Should_SwitchTemplates_When_NodeFromAnotherTemplateIsSelected()
    {
        var cfg = Config();
        var root = TreeNodeViewModel.FromTemplateList(cfg.Templates);
        var fromFirst = Flatten(root.Children[0]).Last();
        var fromSecond = Flatten(root.Children[1]).Last();

        var sut = new TreeSelectionCoordinator();
        sut.ActivateTemplate(fromFirst);
        sut.ActivateTemplate(fromSecond);

        Assert.Same(fromSecond, sut.ActiveNode);
        Assert.Same(fromSecond, sut.ExecTarget);
    }
}
