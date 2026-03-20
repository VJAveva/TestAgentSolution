using Microsoft.Extensions.Logging.Abstractions;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class VocabularyMonitorTests : IDisposable
{
    private readonly VocabularyMonitor _monitor;
    private readonly string _tempDir;

    public VocabularyMonitorTests()
    {
        _monitor = new VocabularyMonitor(NullLogger<VocabularyMonitor>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"VocabMonTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteVocabularyFile(string name, string? xml = null)
    {
        xml ??= """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="TestItem" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="cmd.exe" Parameters="/c echo hello" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, xml);
        return path;
    }

    // ???????????????????????????????????????????????????????????????????
    // StartMonitoring loads config
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void StartMonitoring_Should_LoadConfig_When_ValidFileProvided()
    {
        var path = WriteVocabularyFile("vocab.xml");

        var config = _monitor.StartMonitoring(path);

        Assert.NotNull(config);
        Assert.Single(config.WatchItems);
        Assert.Equal("TestItem", config.WatchItems[0].Tag);
    }

    [Fact]
    public void StartMonitoring_Should_SetCurrentConfig_When_Called()
    {
        var path = WriteVocabularyFile("vocab.xml");

        _monitor.StartMonitoring(path);

        Assert.NotNull(_monitor.CurrentConfig);
        Assert.Single(_monitor.CurrentConfig.WatchItems);
    }

    [Fact]
    public void StartMonitoring_Should_ParseTemplates_When_Present()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <Templates>
                <Template ID="T1">
                  <Action Type="RunCommand" Command="cmd.exe" />
                </Template>
              </Templates>
              <WatchItem Tag="Item1" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Ref TemplateID="T1" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteVocabularyFile("withtemplate.xml", xml);

        var config = _monitor.StartMonitoring(path);

        Assert.Single(config.Templates);
        Assert.Equal("T1", config.Templates[0].ID);
    }

    // ???????????????????????????????????????????????????????????????????
    // SuppressNextReload prevents reload on next file change
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task SuppressNextReload_Should_PreventReload_When_FileSaved()
    {
        var path = WriteVocabularyFile("suppress.xml");
        _monitor.StartMonitoring(path);

        var reloadCount = 0;
        _monitor.ConfigReloaded += _ => Interlocked.Increment(ref reloadCount);

        // Suppress the next reload
        _monitor.SuppressNextReload();

        // Modify the file (which would normally trigger a reload)
        var updatedXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Updated" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="updated.bat" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        File.WriteAllText(path, updatedXml);

        // Wait for debounce period + buffer
        await Task.Delay(1000);

        Assert.Equal(0, reloadCount);
    }

    [Fact]
    public async Task SuppressNextReload_Should_AllowSubsequentReloads_When_OnlyFirstSuppressed()
    {
        var path = WriteVocabularyFile("suppress-once.xml");
        _monitor.StartMonitoring(path);

        var reloadedConfigs = new List<WatchListConfig>();
        _monitor.ConfigReloaded += config => reloadedConfigs.Add(config);

        // Suppress the first change
        _monitor.SuppressNextReload();
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Suppressed" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
            </WatchList>
            """);
        // Wait long enough for suppression window (1s) + debounce (500ms) to fully elapse
        await Task.Delay(2000);

        // Second change should NOT be suppressed
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Reloaded" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
            </WatchList>
            """);
        await Task.Delay(1500);

        Assert.Single(reloadedConfigs);
        Assert.Equal("Reloaded", reloadedConfigs[0].WatchItems[0].Tag);
    }

    // ???????????????????????????????????????????????????????????????????
    // File change triggers ConfigReloaded after 500ms debounce
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public async Task ConfigReloaded_Should_Fire_When_FileModified()
    {
        var path = WriteVocabularyFile("reload.xml");
        _monitor.StartMonitoring(path);

        var tcs = new TaskCompletionSource<WatchListConfig>();
        _monitor.ConfigReloaded += config => tcs.TrySetResult(config);

        // Modify the file
        var newXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Modified" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="modified.bat" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        File.WriteAllText(path, newXml);

        // Wait for debounce (500ms) + some margin
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));

        Assert.Equal(tcs.Task, completedTask);
        var reloaded = await tcs.Task;
        Assert.Single(reloaded.WatchItems);
        Assert.Equal("Modified", reloaded.WatchItems[0].Tag);
    }

    [Fact]
    public async Task ConfigReloaded_Should_UpdateCurrentConfig_When_FileModified()
    {
        var path = WriteVocabularyFile("update-current.xml");
        _monitor.StartMonitoring(path);

        Assert.Equal("TestItem", _monitor.CurrentConfig!.WatchItems[0].Tag);

        var tcs = new TaskCompletionSource<WatchListConfig>();
        _monitor.ConfigReloaded += config => tcs.TrySetResult(config);

        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="NewTag" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Created" ExecutionType="Parallel" />
              </WatchItem>
            </WatchList>
            """);

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Equal(tcs.Task, completedTask);

        Assert.Equal("NewTag", _monitor.CurrentConfig!.WatchItems[0].Tag);
    }

    [Fact]
    public async Task ConfigReloaded_Should_Debounce_When_RapidChangesOccur()
    {
        var path = WriteVocabularyFile("debounce.xml");
        _monitor.StartMonitoring(path);

        var reloadCount = 0;
        WatchListConfig? lastConfig = null;
        _monitor.ConfigReloaded += config =>
        {
            Interlocked.Increment(ref reloadCount);
            lastConfig = config;
        };

        // Rapid-fire changes — only the last one should survive after debounce
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(path, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <WatchList>
                  <WatchItem Tag="Rapid{i}" Path="C:\Triggers" Filter="*.txt">
                    <Event Type="Renamed" ExecutionType="Sequential" />
                  </WatchItem>
                </WatchList>
                """);
            await Task.Delay(50); // Much less than 500ms debounce
        }

        // Wait for debounce to settle
        await Task.Delay(1500);

        // Should have reloaded only once (or possibly twice due to FSW batching),
        // but definitely not 5 times
        Assert.True(reloadCount <= 2, $"Expected at most 2 reloads due to debounce, but got {reloadCount}");
        Assert.NotNull(lastConfig);
    }
}
