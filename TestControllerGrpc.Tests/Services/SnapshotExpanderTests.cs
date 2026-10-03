using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// A session's snapshot keeps Ref nodes unexpanded, so every "what will this run do?" view walked
/// straight past them. On the JVGR22 Sanity pipeline - four Refs and one inline action - the
/// dashboard previewed a single pill and no agent rows for the whole 4-minute revert.
/// </summary>
public class SnapshotExpanderTests
{
    private static ActionConfig Action(string tag, string agent = "") =>
        new() { Tag = tag, AgentName = agent, Type = ActionType.RunRemoteCommand };

    /// <summary>Mirrors the real pipeline: one group of Refs plus an inline SendMail.</summary>
    private static (List<IActionNode> Nodes, Dictionary<string, TemplateConfig> Templates) Jvgr22Shape()
    {
        var templates = new Dictionary<string, TemplateConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["RevertSanity"] = new()
            {
                ID = "RevertSanity",
                Children =
                [
                    new ActionGroupConfig
                    {
                        Tag = "Sanity - Revert all nodes",
                        Children = [Action("Revert jvgr1"), Action("Revert jvgr2")],
                    },
                    new ActionConfig { Tag = "Settle 4 minutes", Type = ActionType.RunCommand },
                ],
            },
            ["PrepSanity"] = new()
            {
                ID = "PrepSanity",
                Children =
                [
                    Action("Copy prep on jvgr1", "jvgr1"),
                    Action("Copy prep on jvgr2", "jvgr2"),
                ],
            },
            ["SmokeSanity"] = new()
            {
                ID = "SmokeSanity",
                Children = [Action("jvgr1 - Run Set1", "jvgr1")],
            },
        };

        List<IActionNode> nodes =
        [
            new ActionGroupConfig
            {
                Tag = "SP2023R2SP2 - Sanity",
                Children =
                [
                    new RefConfig { TemplateID = "RevertSanity" },
                    new RefConfig { TemplateID = "PrepSanity" },
                    new RefConfig { TemplateID = "SmokeSanity" },
                ],
            },
            new ActionConfig { Tag = "Sending Email", Type = ActionType.SendMail },
        ];

        return (nodes, templates);
    }

    private static List<ActionConfig> Leaves(IReadOnlyList<IActionNode> nodes)
    {
        var acc = new List<ActionConfig>();
        Walk(nodes, acc);
        return acc;

        static void Walk(IReadOnlyList<IActionNode> nodes, List<ActionConfig> acc)
        {
            foreach (var n in nodes)
            {
                if (n is ActionConfig a) acc.Add(a);
                else if (n is ActionGroupConfig g) Walk(g.Children, acc);
            }
        }
    }

    [Fact]
    public void ExpandForDisplay_Should_RevealEveryActionBehindARef()
    {
        var (nodes, templates) = Jvgr22Shape();

        var before = Leaves(nodes).Select(a => a.Tag).ToList();
        var after = Leaves(SnapshotExpander.ExpandForDisplay(nodes, templates)).Select(a => a.Tag).ToList();

        // This is the bug, stated plainly: without expansion only the inline action is visible.
        Assert.Equal(["Sending Email"], before);

        Assert.Contains("Revert jvgr1", after);
        Assert.Contains("Settle 4 minutes", after);
        Assert.Contains("Copy prep on jvgr1", after);
        Assert.Contains("jvgr1 - Run Set1", after);
        Assert.Contains("Sending Email", after);
        Assert.Equal(7, after.Count);
    }

    [Fact]
    public void ExpandForDisplay_Should_RevealEveryAgent_SoTheFleetRowsAppear()
    {
        var (nodes, templates) = Jvgr22Shape();

        var agents = Leaves(SnapshotExpander.ExpandForDisplay(nodes, templates))
            .Select(a => a.AgentName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        Assert.Equal(["jvgr1", "jvgr2"], agents);
    }

    [Fact]
    public void ExpandForDisplay_Should_KeepGroupNestingAndOrder()
    {
        var (nodes, templates) = Jvgr22Shape();
        var expanded = SnapshotExpander.ExpandForDisplay(nodes, templates);

        var top = Assert.IsType<ActionGroupConfig>(expanded[0]);
        Assert.Equal("SP2023R2SP2 - Sanity", top.Tag);

        // The revert group inside the template must survive as a group, not be flattened away.
        var revertGroup = top.Children.OfType<ActionGroupConfig>().First();
        Assert.Equal("Sanity - Revert all nodes", revertGroup.Tag);
        Assert.Equal(["Revert jvgr1", "Revert jvgr2"],
            revertGroup.Children.OfType<ActionConfig>().Select(a => a.Tag));

        Assert.Equal("Sending Email", Assert.IsType<ActionConfig>(expanded[1]).Tag);
    }

    [Fact]
    public void CountLeafActions_Should_CountThroughRefs_SoProgressIsNotOverstated()
    {
        var (nodes, templates) = Jvgr22Shape();

        // The old count used SnapshotNodes.Count = 2 top-level nodes, so one finished action
        // reported 50% of a seven-action run.
        Assert.Equal(2, nodes.Count);
        Assert.Equal(7, SnapshotExpander.CountLeafActions(nodes, templates));
    }

    [Fact]
    public void ExpandForDisplay_Should_NotMutateTheSnapshot()
    {
        var (nodes, templates) = Jvgr22Shape();
        var originalTop = (ActionGroupConfig)nodes[0];
        var childCountBefore = originalTop.Children.Count;

        SnapshotExpander.ExpandForDisplay(nodes, templates);

        // SnapshotNodes is frozen for retry and node addressing - expansion must leave it alone.
        Assert.Equal(childCountBefore, originalTop.Children.Count);
        Assert.All(originalTop.Children, c => Assert.IsType<RefConfig>(c));
    }

    [Fact]
    public void ExpandForDisplay_Should_DropAnUnknownTemplate_RatherThanInventActions()
    {
        List<IActionNode> nodes = [new RefConfig { TemplateID = "DoesNotExist" }];

        var expanded = SnapshotExpander.ExpandForDisplay(
            nodes, new Dictionary<string, TemplateConfig>(StringComparer.OrdinalIgnoreCase));

        // The executor logs and skips an unknown template, so previewing it would be a lie.
        Assert.Empty(expanded);
    }

    [Fact]
    public void ExpandForDisplay_Should_Terminate_When_ATemplateRefsItself()
    {
        var templates = new Dictionary<string, TemplateConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Loop"] = new()
            {
                ID = "Loop",
                Children = [Action("inner"), new RefConfig { TemplateID = "Loop" }],
            },
        };
        List<IActionNode> nodes = [new RefConfig { TemplateID = "Loop" }];

        var expanded = SnapshotExpander.ExpandForDisplay(nodes, templates);

        Assert.Single(Leaves(expanded));
    }

    [Fact]
    public void ExpandForDisplay_Should_BeSafe_When_ThereAreNoTemplates()
    {
        var (nodes, _) = Jvgr22Shape();

        var expanded = SnapshotExpander.ExpandForDisplay(nodes, null);

        // A session restored from disk has no templates; it must degrade, not throw.
        Assert.Equal(["Sending Email"], Leaves(expanded).Select(a => a.Tag));
    }
}
