using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// After a run ends, steps it never reached (stop on first failure) must not keep spinning in the tree.
/// </summary>
public class TreeRunSettleTests
{
    private static TreeNodeViewModel Node(string kind, string status, params TreeNodeViewModel[] children)
    {
        var node = new TreeNodeViewModel { NodeKind = kind };
        foreach (var c in children)
        {
            c.Parent = node;
            node.Children.Add(c);
        }
        node.ExecutionStatus = status;
        return node;
    }

    [Fact]
    public void SettleAfterRun_Should_MarkUnreachedStepsNotRun_When_AnEarlierStepFailed()
    {
        var failed = Node(NodeKinds.Action, "Failed");
        var unreached = Node(NodeKinds.Action, "Running");
        var unreachedGroup = Node(NodeKinds.ActionGroup, "Running", Node(NodeKinds.Action, "Running"));
        var stage = Node(NodeKinds.ActionGroup, "Running", failed, unreached, unreachedGroup);
        var pipeline = Node(NodeKinds.WatchItem, "Running", stage);

        var changed = pipeline.SettleAfterRun();

        Assert.Equal("Failed", failed.ExecutionStatus);
        Assert.Equal("NotRun", unreached.ExecutionStatus);
        Assert.Equal("NotRun", unreachedGroup.ExecutionStatus);
        Assert.Equal("Failed", stage.ExecutionStatus);
        Assert.Equal("Failed", pipeline.ExecutionStatus);
        Assert.Equal(5, changed);
    }

    [Fact]
    public void SettleAfterRun_Should_ShowPartialFailure_When_SomeStepsSucceededAndTheRestWereNotRun()
    {
        var group = Node(NodeKinds.ActionGroup, "Running", Node(NodeKinds.Action, "Success"), Node(NodeKinds.Action, "Running"));

        group.SettleAfterRun();

        Assert.Equal("PartialFailure", group.ExecutionStatus);
    }

    [Fact]
    public void SettleAfterRun_Should_ChangeNothing_When_NoNodeIsStillRunning()
    {
        var pipeline = Node(NodeKinds.WatchItem, "Success", Node(NodeKinds.Action, "Success"), Node(NodeKinds.Action, "Skipped"));

        Assert.Equal(0, pipeline.SettleAfterRun());
        Assert.Equal("Success", pipeline.ExecutionStatus);
    }

    [Fact]
    public void NotRun_Should_ExplainItself_When_Hovered()
    {
        var node = Node(NodeKinds.Action, "NotRun");

        Assert.Contains("ended before this step was reached", node.StatusTooltip);
    }
}
