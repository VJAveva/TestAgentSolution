using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for NodeAddressing: locating a pipeline node by structural path, detecting a tree that
/// changed under the caller, and finding the Initialize a node inherits.
/// </summary>
public class NodeAddressingTests
{
    // e0            Renamed
    //   c0          Initialize "RootInit"
    //   c1          ActionGroup "Phase1"
    //       c0      Action "warmgr"
    //       c1      ActionGroup "WarmPool"
    //           c0  Initialize "PoolInit"
    //           c1  Action "warmhist"
    //   c2          Ref -> "RevertSanityAgents"
    private static WatchItemConfig BuildItem() => new()
    {
        Tag = "Revert 9 Nodes",
        Events =
        [
            new EventConfig
            {
                Type = "Renamed",
                Children =
                [
                    new InitializeConfig { Tag = "RootInit", ParameterFile = @"C:\p\root.txt" },
                    new ActionGroupConfig
                    {
                        Tag = "Phase1",
                        Children =
                        [
                            new ActionConfig { Tag = "Revert warmgr", AgentName = "warmgr" },
                            new ActionGroupConfig
                            {
                                Tag = "WarmPool",
                                Children =
                                [
                                    new InitializeConfig { Tag = "PoolInit", ParameterFile = @"C:\p\pool.txt" },
                                    new ActionConfig { Tag = "Revert warmhist", AgentName = "warmhist" },
                                ],
                            },
                        ],
                    },
                    new RefConfig { TemplateID = "RevertSanityAgents" },
                ],
            },
        ],
    };

    // ── Resolution ───────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_Should_ReturnEvent_When_PathIsEventOnly()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0", out var resolved, out _));

        Assert.Equal(RunnableNodeKind.Event, resolved!.Kind);
        Assert.Equal("Renamed", resolved.DisplayName);
        Assert.Null(resolved.Node);
    }

    [Fact]
    public void TryResolve_Should_ReturnGroup_When_PathNamesActionGroup()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0/c1", out var resolved, out _));

        Assert.Equal(RunnableNodeKind.Group, resolved!.Kind);
        Assert.Equal("Phase1", resolved.DisplayName);
    }

    [Fact]
    public void TryResolve_Should_ReturnAction_When_PathNamesNestedAction()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0/c1/c1/c1", out var resolved, out _));

        Assert.Equal(RunnableNodeKind.Action, resolved!.Kind);
        Assert.Equal("Revert warmhist", resolved.DisplayName);
    }

    [Fact]
    public void TryResolve_Should_ReturnTemplate_When_PathNamesRef()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0/c2", out var resolved, out _));

        Assert.Equal(RunnableNodeKind.Template, resolved!.Kind);
        Assert.Equal("RevertSanityAgents", resolved.DisplayName);
    }

    [Fact]
    public void TryResolve_Should_Fail_When_PathNamesInitialize()
    {
        Assert.False(NodeAddressing.TryResolve(BuildItem(), "e0/c0", out var resolved, out var error));

        Assert.Null(resolved);
        Assert.Contains("setup step", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("c0")]          // must start at an event
    [InlineData("e0/e1")]       // only the first segment is an event
    [InlineData("eX")]
    [InlineData("e0/c")]
    [InlineData("e-1")]
    [InlineData("../../etc")]
    public void TryResolve_Should_Fail_When_PathIsMalformed(string path)
    {
        Assert.False(NodeAddressing.TryResolve(BuildItem(), path, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("e9")]
    [InlineData("e0/c9")]
    [InlineData("e0/c1/c1/c9")]
    [InlineData("e0/c0/c0")]    // Initialize has no children
    public void TryResolve_Should_Fail_When_PathIsOutOfRange(string path)
    {
        Assert.False(NodeAddressing.TryResolve(BuildItem(), path, out _, out var error));
        Assert.NotEmpty(error);
    }

    // ── Round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void PathOf_Should_RoundTrip_When_NodeIsInTheTree()
    {
        var item = BuildItem();
        var group = (ActionGroupConfig)item.Events[0].Children[1];
        var nested = (ActionGroupConfig)group.Children[1];
        var action = nested.Children[1];

        var path = NodeAddressing.PathOf(item, action);

        Assert.Equal("e0/c1/c1/c1", path);
        Assert.True(NodeAddressing.TryResolve(item, path, out var resolved, out _));
        Assert.Same(action, resolved!.Node);
    }

    [Fact]
    public void PathOf_Should_ReturnNull_When_NodeIsNotInTheTree()
    {
        Assert.Null(NodeAddressing.PathOf(BuildItem(), new ActionConfig { Tag = "stranger" }));
    }

    // ── Initialize inheritance ───────────────────────────────────────────

    [Fact]
    public void NearestInitializeFor_Should_PreferInnermost_When_AncestorsBothDeclareOne()
    {
        var init = NodeAddressing.NearestInitializeFor(BuildItem(), "e0/c1/c1/c1");

        Assert.Equal("PoolInit", init?.Tag);
    }

    [Fact]
    public void NearestInitializeFor_Should_WalkOutward_When_OwnGroupHasNone()
    {
        // e0/c1/c0 sits in Phase1, which declares no Initialize, so the event's is inherited.
        var init = NodeAddressing.NearestInitializeFor(BuildItem(), "e0/c1/c0");

        Assert.Equal("RootInit", init?.Tag);
    }

    [Fact]
    public void NearestInitializeFor_Should_ReturnNull_When_NoAncestorDeclaresOne()
    {
        var item = new WatchItemConfig
        {
            Tag = "Bare",
            Events = [new EventConfig { Type = "Renamed", Children = [new ActionConfig { Tag = "solo" }] }],
        };

        Assert.Null(NodeAddressing.NearestInitializeFor(item, "e0/c0"));
    }

    [Fact]
    public void NearestInitializeFor_Should_ReturnNull_When_TargetIsTheEvent()
    {
        // The event's own Initialize is one of its children, so running the event already runs it.
        Assert.Null(NodeAddressing.NearestInitializeFor(BuildItem(), "e0"));
    }

    [Fact]
    public void NearestInitializeFor_Should_SearchAncestorsOnly_When_TargetIsAGroup()
    {
        // WarmPool declares PoolInit as its own child; running the group runs it. The option must
        // therefore offer the ancestor's RootInit, not the group's own.
        Assert.Equal("RootInit", NodeAddressing.NearestInitializeFor(BuildItem(), "e0/c1/c1")?.Tag);
    }

    // ── Revision ─────────────────────────────────────────────────────────

    [Fact]
    public void RevisionOf_Should_BeStable_When_TreeIsRebuiltIdentically()
    {
        Assert.Equal(NodeAddressing.RevisionOf(BuildItem()), NodeAddressing.RevisionOf(BuildItem()));
    }

    [Fact]
    public void RevisionOf_Should_IgnoreNodeId_When_NodesAreRegenerated()
    {
        var a = BuildItem();
        var b = BuildItem();

        // NodeId is a fresh Guid per construction; it must not leak into the revision or every
        // hot-reload would invalidate every client's tree.
        Assert.NotEqual(a.Events[0].Children[1].NodeId, b.Events[0].Children[1].NodeId);
        Assert.Equal(NodeAddressing.RevisionOf(a), NodeAddressing.RevisionOf(b));
    }

    [Fact]
    public void RevisionOf_Should_Change_When_NodeOrderChanges()
    {
        var item = BuildItem();
        var before = NodeAddressing.RevisionOf(item);

        var children = item.Events[0].Children;
        (children[1], children[2]) = (children[2], children[1]);

        Assert.NotEqual(before, NodeAddressing.RevisionOf(item));
    }

    [Fact]
    public void RevisionOf_Should_Change_When_NodeIsInserted()
    {
        var item = BuildItem();
        var before = NodeAddressing.RevisionOf(item);

        item.Events[0].Children.Insert(0, new ActionConfig { Tag = "new first" });

        Assert.NotEqual(before, NodeAddressing.RevisionOf(item));
    }

    [Fact]
    public void RevisionOf_Should_Change_When_ActionCommandChanges()
    {
        var item = BuildItem();
        var before = NodeAddressing.RevisionOf(item);

        var group = (ActionGroupConfig)item.Events[0].Children[1];
        ((ActionConfig)group.Children[0]).Command = @"C:\changed.bat";

        Assert.NotEqual(before, NodeAddressing.RevisionOf(item));
    }

    // ── Session labelling ────────────────────────────────────────────────

    [Theory]
    [InlineData("e0", "Renamed")]
    [InlineData("e0/c1", "Group:Phase1")]
    [InlineData("e0/c1/c0", "Action:Revert warmgr")]
    [InlineData("e0/c2", "Group:RevertSanityAgents")]
    public void SessionEventTypeFor_Should_MarkScopedRuns_When_NodeIsNotTheEvent(string path, string expected)
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), path, out var resolved, out _));

        var eventType = NodeAddressing.SessionEventTypeFor(resolved!);

        Assert.Equal(expected, eventType);

        // The prefix is the only thing that keeps a node-run out of full-run reporting.
        var session = new ExecutionSession { EventType = eventType };
        Assert.Equal(path == "e0", session.IsFullPipelineRun);
    }

    [Fact]
    public void RunChildrenOf_Should_ReturnGroupChildren_When_NodeIsAGroup()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0/c1", out var resolved, out _));

        Assert.Equal(2, NodeAddressing.RunChildrenOf(resolved!).Count);
    }

    [Fact]
    public void RunChildrenOf_Should_ReturnTheNodeAlone_When_NodeIsAnAction()
    {
        Assert.True(NodeAddressing.TryResolve(BuildItem(), "e0/c1/c0", out var resolved, out _));

        var children = NodeAddressing.RunChildrenOf(resolved!);

        Assert.Single(children);
        Assert.Same(resolved!.Node, children[0]);
    }
}
