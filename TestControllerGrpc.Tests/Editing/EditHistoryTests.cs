using TestControllerGrpc.Editing;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Editing;

public class EditHistoryTests
{
    private static WatchListConfig Config() => new()
    {
        FilePath = @"C:\wl\WatchList.xml",
        WatchItems = [new WatchItemConfig { Tag = "P1", Path = @"C:\t", Filter = "*.txt" }],
    };

    [Fact]
    public void UndoRedo_Should_RestoreEachState_When_AnEditWasApplied()
    {
        var history = new EditHistory();
        var config = Config();

        Assert.True(history.Apply(config, "Add WatchItem", () =>
            config.WatchItems.Add(new WatchItemConfig { Tag = "P2", Path = @"C:\t", Filter = "*.txt" })));

        var undone = history.Undo(config)!;
        Assert.Equal(["P1"], undone.WatchItems.Select(w => w.Tag));
        Assert.Equal(config.FilePath, undone.FilePath);
        Assert.Equal("Add WatchItem", history.RedoLabel);

        var redone = history.Redo(undone)!;
        Assert.Equal(["P1", "P2"], redone.WatchItems.Select(w => w.Tag));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_Should_ReturnNull_When_NothingWasRecorded()
    {
        var history = new EditHistory();
        var config = Config();

        Assert.False(history.Apply(config, "No-op", () => { }));
        Assert.Null(history.Undo(config));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Apply_Should_KeepOnlyTheLast50Steps_And_ClearRedo_When_ANewEditFollowsAnUndo()
    {
        var history = new EditHistory();
        var config = Config();
        for (var i = 0; i < EditHistory.Capacity + 5; i++)
            history.Apply(config, $"Edit {i}", () => config.WatchItems[0].Filter = $"*.{i}");

        Assert.Equal(EditHistory.Capacity, history.UndoCount);

        var undone = history.Undo(config)!;
        history.Apply(undone, "Other", () => undone.WatchItems[0].Tag = "Renamed");
        Assert.False(history.CanRedo);
    }
}
