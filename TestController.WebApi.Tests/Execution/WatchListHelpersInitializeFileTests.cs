using TestController.Api;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Execution;

/// <summary>
/// A WatchItem may initialise several parameter files (e.g. a Warm stage and a Sanity stage).
/// Applying a triggered build number to only the first left the later stages running the previous
/// build — a silent wrong-build run rather than a visible failure.
/// </summary>
public class WatchListHelpersInitializeFileTests
{
    private static WatchItemConfig Item(params IActionNode[] children) =>
        new() { Tag = "pipeline", Events = [new EventConfig { Type = "Created", Children = [.. children] }] };

    private static InitializeConfig Init(string file) => new() { ParameterFile = file };

    [Fact]
    public void FindInitializeFiles_Should_ReturnEveryFile_When_WatchItemHasSeveralStages()
    {
        var item = Item(
            Init(@"C:\Params\WarmVariables.txt"),
            Init(@"C:\Params\SanityVariables.txt"));

        Assert.Equal(
            [@"C:\Params\WarmVariables.txt", @"C:\Params\SanityVariables.txt"],
            WatchListHelpers.FindInitializeFiles(item));
    }

    [Fact]
    public void FindInitializeFiles_Should_DescendIntoActionGroups()
    {
        var item = Item(
            Init(@"C:\Params\Top.txt"),
            new ActionGroupConfig
            {
                Tag = "group",
                Children = [Init(@"C:\Params\Nested.txt"),
                            new ActionGroupConfig { Tag = "inner", Children = [Init(@"C:\Params\Deep.txt")] }],
            });

        Assert.Equal(
            [@"C:\Params\Top.txt", @"C:\Params\Nested.txt", @"C:\Params\Deep.txt"],
            WatchListHelpers.FindInitializeFiles(item));
    }

    [Fact]
    public void FindInitializeFiles_Should_DeduplicateIgnoringCase()
    {
        var item = Item(
            Init(@"C:\Params\Warm.txt"),
            Init(@"c:\params\WARM.TXT"),
            Init(@"C:\Params\Sanity.txt"));

        Assert.Equal(
            [@"C:\Params\Warm.txt", @"C:\Params\Sanity.txt"],
            WatchListHelpers.FindInitializeFiles(item));
    }

    [Fact]
    public void FindInitializeFiles_Should_SpanAllEvents()
    {
        var item = new WatchItemConfig
        {
            Tag = "pipeline",
            Events =
            [
                new EventConfig { Type = "Created", Children = [Init(@"C:\Params\A.txt")] },
                new EventConfig { Type = "Renamed", Children = [Init(@"C:\Params\B.txt")] },
            ],
        };

        Assert.Equal([@"C:\Params\A.txt", @"C:\Params\B.txt"], WatchListHelpers.FindInitializeFiles(item));
    }

    [Fact]
    public void FindInitializeFiles_Should_IgnoreInitializeNodesWithoutAFile()
    {
        var item = Item(Init(""), Init("   "), Init(@"C:\Params\Real.txt"));
        Assert.Equal([@"C:\Params\Real.txt"], WatchListHelpers.FindInitializeFiles(item));
    }

    [Fact]
    public void FindInitializeFiles_Should_ReturnEmpty_When_NoInitializeNode()
    {
        Assert.Empty(WatchListHelpers.FindInitializeFiles(Item(new ActionGroupConfig { Tag = "g" })));
    }

    [Fact]
    public void FindInitializeFile_Should_StillReturnTheFirstFile()
    {
        var item = Item(Init(@"C:\Params\First.txt"), Init(@"C:\Params\Second.txt"));
        Assert.Equal(@"C:\Params\First.txt", WatchListHelpers.FindInitializeFile(item));
    }

    [Fact]
    public void FindInitializeFile_Should_ReturnNull_When_NoInitializeNode()
    {
        Assert.Null(WatchListHelpers.FindInitializeFile(Item()));
    }
}
