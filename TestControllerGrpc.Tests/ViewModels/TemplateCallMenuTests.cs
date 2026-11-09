using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;
using TestControllerGrpc.Views;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// P1 of the context-menu requirement: a right-click targets the row under the mouse, and a Ref plus
/// the template nodes mirrored under it offer only template-call actions, never edits that would
/// rewrite the shared template.
/// </summary>
[Collection("TokenState")]
public class TemplateCallMenuTests : IDisposable
{
    public TemplateCallMenuTests()
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

    /// <summary>Pipeline P1 calls Outer; Outer holds a group that calls Inner. P2 also calls Outer.</summary>
    private static WatchListConfig NestedCalls() => new()
    {
        WatchItems = [Pipeline("P1"), Pipeline("P2")],
        Templates =
        [
            new TemplateConfig
            {
                ID = "Outer",
                Children =
                [
                    new ActionGroupConfig
                    {
                        Tag = "Prep",
                        Children = [new RefConfig { TemplateID = "Inner" }],
                    },
                ],
            },
            new TemplateConfig
            {
                ID = "Inner",
                Children = [new ActionConfig { Tag = "Install", Type = ActionType.RunCommand, Command = "x.bat" }],
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
                Children =
                [
                    new ActionConfig { Tag = "Own", Type = ActionType.RunCommand, Command = "own.bat" },
                    new RefConfig { TemplateID = "Outer" },
                ],
            },
        ],
    };

    private static TreeNodeViewModel EventOf(TreeNodeViewModel root, string tag)
        => root.Children.Single(c => c.Tag == tag).Children[0];

    private static TreeNodeViewModel CallIn(TreeNodeViewModel root, string tag)
        => EventOf(root, tag).Children.Single(c => c.NodeKind == NodeKinds.Ref);

    [Fact]
    public void OwningTemplateCall_Should_BeTheRefItself_When_TheNodeIsThePipelinesRef()
    {
        var call = CallIn(TreeNodeViewModel.FromWatchList(NestedCalls()), "P1");

        Assert.Same(call, call.OwningTemplateCall);
        Assert.Equal("Outer", call.CalledTemplateId);
    }

    [Fact]
    public void OwningTemplateCall_Should_BeThePipelinesRef_When_TheNodeIsDeepInsideANestedExpansion()
    {
        var call = CallIn(TreeNodeViewModel.FromWatchList(NestedCalls()), "P1");
        var group = call.Children[0];
        var nestedRef = group.Children[0];
        var install = nestedRef.Children[0];

        Assert.Same(call, group.OwningTemplateCall);
        Assert.Same(call, nestedRef.OwningTemplateCall);
        Assert.Same(call, install.OwningTemplateCall);
    }

    [Fact]
    public void CalledTemplateId_Should_BeTheNearestTemplate_When_TheNodeIsInsideANestedCall()
    {
        var call = CallIn(TreeNodeViewModel.FromWatchList(NestedCalls()), "P1");
        var group = call.Children[0];
        var install = group.Children[0].Children[0];

        Assert.Equal("Outer", group.CalledTemplateId);
        Assert.Equal("Inner", install.CalledTemplateId);
    }

    [Fact]
    public void OwningTemplateCall_Should_BeNull_When_TheNodeIsAPipelinesOwnNode()
    {
        var root = TreeNodeViewModel.FromWatchList(NestedCalls());
        var ev = EventOf(root, "P1");

        Assert.Null(root.OwningTemplateCall);
        Assert.Null(root.Children[0].OwningTemplateCall);
        Assert.Null(ev.OwningTemplateCall);
        Assert.Null(ev.Children.Single(c => c.Tag == "Own").OwningTemplateCall);
        Assert.Null(ev.Children.Single(c => c.Tag == "Own").CalledTemplateId);
    }

    [Fact]
    public void CalledTemplateId_Should_BeNull_When_TheRefNamesNoTemplate()
    {
        var config = NestedCalls();
        ((RefConfig)config.WatchItems[0].Events[0].Children[1]).TemplateID = "";

        var call = CallIn(TreeNodeViewModel.FromWatchList(config), "P1");

        Assert.Same(call, call.OwningTemplateCall);
        Assert.Null(call.CalledTemplateId);
    }

    [Fact]
    public void TryDetach_Should_RemoveOnlyThisPipelinesCall_When_DeletingTheCallOfAMirroredNode()
    {
        var config = NestedCalls();
        var root = TreeNodeViewModel.FromWatchList(config);
        var install = CallIn(root, "P1").Children[0].Children[0].Children[0];
        var call = install.OwningTemplateCall!;

        Assert.True(MainViewModel.TryDetach(call));

        Assert.DoesNotContain(config.WatchItems[0].Events[0].Children, c => c is RefConfig);
        Assert.DoesNotContain(EventOf(root, "P1").Children, c => c.NodeKind == NodeKinds.Ref);
        Assert.Contains(config.WatchItems[0].Events[0].Children, c => c is ActionConfig { Tag: "Own" });
        Assert.Contains(config.WatchItems[1].Events[0].Children, c => c is RefConfig { TemplateID: "Outer" });
        Assert.Single(config.Templates[0].Children);
        Assert.Single(config.Templates[1].Children);
    }

    [Fact]
    public void TemplateCallRemovalPrompt_Should_NameTheTemplateAndThePipeline()
    {
        var call = CallIn(TreeNodeViewModel.FromWatchList(NestedCalls()), "P2");

        var prompt = MainViewModel.TemplateCallRemovalPrompt(call);

        Assert.Contains("'Outer'", prompt);
        Assert.Contains("'P2'", prompt);
        Assert.Contains("template is not deleted", prompt);
    }

    // ── Right-click hit test ────────────────────────────────────────────────

    [Fact]
    public void ContainerAt_Should_ReturnTheNestedRow_When_TheClickLandsOnAChildsText()
    {
        RunSta(() =>
        {
            var run = new Run("child");
            var childText = new TextBlock(run);
            var child = new TreeViewItem { Header = childText };
            var parent = new TreeViewItem { Header = "parent", IsExpanded = true };
            parent.Items.Add(child);
            var tree = new TreeView();
            tree.Items.Add(parent);
            tree.Measure(new Size(400, 400));
            tree.Arrange(new Rect(0, 0, 400, 400));
            tree.UpdateLayout();

            Assert.Same(child, TreeHitTest.ContainerAt(childText));
            Assert.Same(child, TreeHitTest.ContainerAt(run));
            Assert.Null(TreeHitTest.ContainerAt(tree));
            Assert.False(TreeHitTest.IsInScrollBar(childText));
        });
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
