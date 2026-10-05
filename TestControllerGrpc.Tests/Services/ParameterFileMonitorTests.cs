using System.IO;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Nothing watched the parameter files: VocabularyMonitor watches WatchList.xml and
/// FileWatcherManager watches the trigger folders, so a value edited in pipeline-config.json left
/// every resolved label stale until the next full tree rebuild.
/// </summary>
public class ParameterFileMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfm_{Guid.NewGuid():N}");

    public ParameterFileMonitorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, string content = "{}")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Watch_Should_TrackTheFile_When_ItIsReferencedByAnInitializeNode()
    {
        var path = WriteFile("pipeline-config.json");
        using var monitor = new ParameterFileMonitor();

        monitor.Watch([path]);

        Assert.Contains(path, monitor.WatchedFiles);
    }

    [Fact]
    public void Watch_Should_IgnorePathsCarryingTokens_Because_TheRealFileIsNotKnowableYet()
    {
        using var monitor = new ParameterFileMonitor();

        monitor.Watch([@"C:\Params\[_Release]\pipeline-config.json"]);

        Assert.Empty(monitor.WatchedFiles);
    }

    [Fact]
    public void Watch_Should_Deduplicate_When_SeveralPipelinesShareOneConfig()
    {
        var path = WriteFile("shared.json");
        using var monitor = new ParameterFileMonitor();

        monitor.Watch([path, path, path]);

        Assert.Single(monitor.WatchedFiles);
    }

    [Fact]
    public void Watch_Should_DropOldFiles_When_TheConfigNoLongerReferencesThem()
    {
        var a = WriteFile("a.json");
        var b = WriteFile("b.json");
        using var monitor = new ParameterFileMonitor();

        monitor.Watch([a, b]);
        monitor.Watch([b]);

        Assert.DoesNotContain(a, monitor.WatchedFiles);
        Assert.Contains(b, monitor.WatchedFiles);
    }

    [Fact]
    public void Watch_Should_SurviveAMissingDirectory_So_ATypoDoesNotStopTheApp()
    {
        using var monitor = new ParameterFileMonitor();

        monitor.Watch([Path.Combine(_dir, "nope", "pipeline-config.json")]);

        Assert.Empty(monitor.WatchedFiles);
    }

    [Fact]
    public async Task ParametersChanged_Should_Fire_When_AWatchedFileIsEdited()
    {
        var path = WriteFile("pipeline-config.json", "{\"a\":1}");
        using var monitor = new ParameterFileMonitor { Debounce = TimeSpan.FromMilliseconds(50) };

        var fired = new TaskCompletionSource();
        monitor.ParametersChanged += () => fired.TrySetResult();
        monitor.Watch([path]);

        File.WriteAllText(path, "{\"a\":2}");

        // Wait on the signal rather than a fixed delay: a filesystem event has no fixed latency.
        var done = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(fired.Task, done);
    }

    [Fact]
    public async Task ParametersChanged_Should_NotFire_When_AnUnrelatedFileInTheSameFolderChanges()
    {
        var watched = WriteFile("pipeline-config.json");
        using var monitor = new ParameterFileMonitor { Debounce = TimeSpan.FromMilliseconds(50) };

        var fired = new TaskCompletionSource();
        monitor.ParametersChanged += () => fired.TrySetResult();
        monitor.Watch([watched]);

        // One watcher covers the whole folder, so it must filter by file.
        File.WriteAllText(Path.Combine(_dir, "unrelated.log"), "noise");

        var done = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.NotSame(fired.Task, done);
    }
}
