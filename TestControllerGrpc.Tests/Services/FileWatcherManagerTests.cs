using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class FileWatcherManagerTests : IDisposable
{
    private readonly Mock<IActionPipelineExecutor> _executor;
    private readonly FileWatcherManager _manager;
    private readonly string _tempDir;

    public FileWatcherManagerTests()
    {
        _executor = new Mock<IActionPipelineExecutor>();
        _manager = new FileWatcherManager(
            _executor.Object,
            new ExecutionSessionManager(),
            NullLogger<FileWatcherManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"FWMTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string CreateSubDir(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ???????????????????????????????????????????????????????????????????
    // ApplyConfig creates watchers for enabled items
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ApplyConfig_Should_CreateWatchers_When_ItemsAreEnabled()
    {
        var dir1 = CreateSubDir("watch1");
        var dir2 = CreateSubDir("watch2");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Item1", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
                new WatchItemConfig { Tag = "Item2", Path = dir2, Filter = "*.xml", IsEnabled = true,
                    Events = [new EventConfig { Type = "Created" }] },
            ]
        };

        _manager.ApplyConfig(config);

        Assert.Equal(2, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Item1"));
        Assert.True(_manager.HasWatcher("Item2"));
    }

    // ???????????????????????????????????????????????????????????????????
    // ApplyConfig skips disabled items
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ApplyConfig_Should_SkipDisabledItems_When_IsEnabledFalse()
    {
        var dir1 = CreateSubDir("enabled");
        var dir2 = CreateSubDir("disabled");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Enabled", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
                new WatchItemConfig { Tag = "Disabled", Path = dir2, Filter = "*.txt", IsEnabled = false,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };

        _manager.ApplyConfig(config);

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Enabled"));
        Assert.False(_manager.HasWatcher("Disabled"));
    }

    [Fact]
    public void ApplyConfig_Should_SkipItems_When_PathIsEmpty()
    {
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "NoPath", Path = "", Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };

        _manager.ApplyConfig(config);

        Assert.Equal(0, _manager.ActiveWatcherCount);
    }

    // ???????????????????????????????????????????????????????????????????
    // AddOrUpdateWatcher replaces existing watcher
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void AddOrUpdateWatcher_Should_ReplaceExisting_When_TagAlreadyExists()
    {
        var dir1 = CreateSubDir("original");
        var dir2 = CreateSubDir("replacement");

        // Set up initial watcher
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Item1", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);
        Assert.Equal(1, _manager.ActiveWatcherCount);

        // Replace with new path
        var updated = new WatchItemConfig
        {
            Tag = "Item1", Path = dir2, Filter = "*.xml", IsEnabled = true,
            Events = [new EventConfig { Type = "Created" }]
        };
        _manager.AddOrUpdateWatcher(updated);

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Item1"));
    }

    [Fact]
    public void AddOrUpdateWatcher_Should_AddNew_When_TagDoesNotExist()
    {
        var dir1 = CreateSubDir("existing");
        var dir2 = CreateSubDir("new");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Existing", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);

        var newItem = new WatchItemConfig
        {
            Tag = "NewItem", Path = dir2, Filter = "*.log", IsEnabled = true,
            Events = [new EventConfig { Type = "Changed" }]
        };
        _manager.AddOrUpdateWatcher(newItem);

        Assert.Equal(2, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Existing"));
        Assert.True(_manager.HasWatcher("NewItem"));
    }

    [Fact]
    public void AddOrUpdateWatcher_Should_RemoveOnly_When_ItemIsDisabled()
    {
        var dir = CreateSubDir("todisable");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Item1", Path = dir, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);
        Assert.Equal(1, _manager.ActiveWatcherCount);

        // Disable the item
        _manager.AddOrUpdateWatcher(new WatchItemConfig { Tag = "Item1", Path = dir, IsEnabled = false });

        Assert.Equal(0, _manager.ActiveWatcherCount);
        Assert.False(_manager.HasWatcher("Item1"));
    }

    // ???????????????????????????????????????????????????????????????????
    // ApplyDiff removes deleted items, adds new ones, preserves unchanged
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ApplyDiff_Should_RemoveDeletedAndAddNew_When_DiffApplied()
    {
        var dirA = CreateSubDir("a");
        var dirB = CreateSubDir("b");
        var dirC = CreateSubDir("c");

        // Initial state: A and B
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "A", Path = dirA, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
                new WatchItemConfig { Tag = "B", Path = dirB, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);
        Assert.Equal(2, _manager.ActiveWatcherCount);

        // Diff: remove A, keep B, add C
        var newItems = new List<WatchItemConfig>
        {
            new() { Tag = "B", Path = dirB, Filter = "*.txt", IsEnabled = true,
                Events = [new EventConfig { Type = "Renamed" }] },
            new() { Tag = "C", Path = dirC, Filter = "*.log", IsEnabled = true,
                Events = [new EventConfig { Type = "Created" }] },
        };

        _manager.ApplyDiff(newItems);

        Assert.Equal(2, _manager.ActiveWatcherCount);
        Assert.False(_manager.HasWatcher("A"));
        Assert.True(_manager.HasWatcher("B"));
        Assert.True(_manager.HasWatcher("C"));
    }

    [Fact]
    public void ApplyDiff_Should_PreserveUnchanged_When_SameConfigApplied()
    {
        var dir = CreateSubDir("stable");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Stable", Path = dir, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);

        // Apply diff with same item — watcher count should remain 1
        _manager.ApplyDiff(
        [
            new WatchItemConfig { Tag = "Stable", Path = dir, Filter = "*.txt", IsEnabled = true,
                Events = [new EventConfig { Type = "Renamed" }] },
        ]);

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Stable"));
    }

    // ???????????????????????????????????????????????????????????????????
    // RemoveWatcher removes specific item
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void RemoveWatcher_Should_RemoveSpecificItem_When_TagExists()
    {
        var dir1 = CreateSubDir("keep");
        var dir2 = CreateSubDir("remove");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Keep", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
                new WatchItemConfig { Tag = "Remove", Path = dir2, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);
        Assert.Equal(2, _manager.ActiveWatcherCount);

        _manager.RemoveWatcher("Remove");

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Keep"));
        Assert.False(_manager.HasWatcher("Remove"));
    }

    [Fact]
    public void RemoveWatcher_Should_DoNothing_When_TagDoesNotExist()
    {
        var dir = CreateSubDir("only");

        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Only", Path = dir, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config);

        _manager.RemoveWatcher("NonExistent");

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.True(_manager.HasWatcher("Only"));
    }

    // ???????????????????????????????????????????????????????????????????
    // ApplyConfig tears down previous watchers
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ApplyConfig_Should_TearDownPrevious_When_CalledMultipleTimes()
    {
        var dir1 = CreateSubDir("first");
        var dir2 = CreateSubDir("second");

        var config1 = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "First", Path = dir1, Filter = "*.txt", IsEnabled = true,
                    Events = [new EventConfig { Type = "Renamed" }] },
            ]
        };
        _manager.ApplyConfig(config1);
        Assert.Equal(1, _manager.ActiveWatcherCount);

        var config2 = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig { Tag = "Second", Path = dir2, Filter = "*.xml", IsEnabled = true,
                    Events = [new EventConfig { Type = "Created" }] },
            ]
        };
        _manager.ApplyConfig(config2);

        Assert.Equal(1, _manager.ActiveWatcherCount);
        Assert.False(_manager.HasWatcher("First"));
        Assert.True(_manager.HasWatcher("Second"));
    }
}
