using TestControllerGrpc.Editing;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Editing;

/// <summary>ContextMenu_Requirement.md P5: structural edits on groups and actions.</summary>
public class NodeEditsTests
{
    private static List<IActionNode> List() =>
    [
        new ActionConfig { Tag = "A", Type = ActionType.RunCommand, Command = "a.bat" },
        new ActionGroupConfig { Tag = "G", Children = [new ActionConfig { Tag = "G1", Type = ActionType.RunCommand, Command = "g.bat" }] },
    ];

    [Fact]
    public void Duplicate_Should_InsertAnIndependentTaggedCopy_When_ANodeIsDuplicated()
    {
        var list = List();

        var at = NodeEdits.Duplicate(list, 1);

        var copy = Assert.IsType<ActionGroupConfig>(list[at]);
        Assert.Equal(2, at);
        Assert.Equal("G (copy)", copy.Tag);
        Assert.NotSame(((ActionGroupConfig)list[1]).Children[0], copy.Children[0]);
    }

    [Fact]
    public void WrapAndUngroup_Should_RoundTrip_When_AppliedInTurn()
    {
        var list = List();

        NodeEdits.Wrap(list, 0);
        Assert.IsType<ActionGroupConfig>(list[0]);
        Assert.True(NodeEdits.Ungroup(list, 0));

        Assert.Equal("A", ((ActionConfig)list[0]).Tag);
        Assert.False(NodeEdits.Ungroup(list, 0));
    }

    [Fact]
    public void FromXml_Should_Throw_When_TheClipboardIsNotOneNode()
    {
        Assert.Throws<FormatException>(() => NodeEdits.FromXml("<Templates />"));
    }

    [Fact]
    public void GroupAsTemplate_Should_PickAFreeId_When_TheGroupTagIsTaken()
    {
        var group = (ActionGroupConfig)List()[1];

        var template = NodeEdits.GroupAsTemplate(group, ["G", "G_2"]);

        Assert.Equal("G_3", template.ID);
        Assert.Single(template.Children);
    }

    [Fact]
    public void Menu_Should_OfferPaste_Only_When_SomethingWasCopied()
    {
        var ctx = new NodeMenuContext { NodeKind = NodeKinds.Action };

        Assert.DoesNotContain(NodeMenuProvider.Build(ctx), i => i.Id == NodeMenuIds.Paste);
        Assert.Contains(NodeMenuProvider.Build(ctx with { HasClipboard = true }), i => i.Id == NodeMenuIds.Paste);
    }
}
