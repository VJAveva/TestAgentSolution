using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class RetryXmlParserTests : IDisposable
{
    private readonly string _tempDir;

    public RetryXmlParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RetryXmlTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteTempXml(string name, string xml)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, xml);
        return path;
    }

    // ?????????????????????????????????????????????????????????????????
    // Parsing retry attributes from XML
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void Load_Should_ParseRetryAttributes_When_FullySpecified()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="Retry" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunRemoteCommand" AgentName="agent1"
                          Command="install.bat" Parameters="/s"
                          Timeout="7200"
                          MaxRetries="3" RetryDelaySeconds="15"
                          RetryBackoff="Fixed" RetryOnExitCodes="-1,1,2" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("retry.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var action = config.WatchItems[0].Events[0].Children[0] as ActionConfig;

        Assert.NotNull(action);
        Assert.Equal(3, action.MaxRetries);
        Assert.Equal(15, action.RetryDelaySeconds);
        Assert.Equal("Fixed", action.RetryBackoff);
        Assert.Equal("-1,1,2", action.RetryOnExitCodes);
    }

    [Fact]
    public void Load_Should_UseRetryDefaults_When_AttributesOmitted()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="NoRetry" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="cmd.exe" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("noretry.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var action = config.WatchItems[0].Events[0].Children[0] as ActionConfig;

        Assert.NotNull(action);
        Assert.Equal(0, action.MaxRetries);
        Assert.Equal(10, action.RetryDelaySeconds);
        Assert.Equal("Exponential", action.RetryBackoff);
        Assert.Equal("", action.RetryOnExitCodes);
    }

    [Fact]
    public void Load_Should_ParseOnlyMaxRetries_When_OnlyMaxRetriesSpecified()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="PartialRetry" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="cmd.exe" MaxRetries="2" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("partial.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var action = config.WatchItems[0].Events[0].Children[0] as ActionConfig;

        Assert.NotNull(action);
        Assert.Equal(2, action.MaxRetries);
        Assert.Equal(10, action.RetryDelaySeconds);  // default
        Assert.Equal("Exponential", action.RetryBackoff);  // default
    }

    // ?????????????????????????????????????????????????????????????????
    // Serialization round-trip
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void Save_Should_RoundTripRetryAttributes_When_MaxRetriesPositive()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "RT", Path = @"C:\", Filter = "*.*",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    ExecutionType = ExecutionMode.Sequential,
                    Children =
                    [
                        new ActionConfig
                        {
                            Type = ActionType.RunRemoteCommand,
                            Command = "install.bat",
                            MaxRetries = 3,
                            RetryDelaySeconds = 20,
                            RetryBackoff = "Fixed",
                            RetryOnExitCodes = "-1,1",
                        }
                    ]
                }
            ]
        });

        var path = Path.Combine(_tempDir, "roundtrip.xml");
        WatchListXmlParser.Save(config, path);
        var reloaded = WatchListXmlParser.Load(path);

        var action = reloaded.WatchItems[0].Events[0].Children[0] as ActionConfig;
        Assert.NotNull(action);
        Assert.Equal(3, action.MaxRetries);
        Assert.Equal(20, action.RetryDelaySeconds);
        Assert.Equal("Fixed", action.RetryBackoff);
        Assert.Equal("-1,1", action.RetryOnExitCodes);
    }

    [Fact]
    public void Save_Should_OmitRetryAttributes_When_MaxRetriesIsZero()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "NoRetry", Path = @"C:\", Filter = "*.*",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    ExecutionType = ExecutionMode.Sequential,
                    Children =
                    [
                        new ActionConfig
                        {
                            Type = ActionType.RunCommand,
                            Command = "cmd.exe",
                            MaxRetries = 0,  // no retry
                        }
                    ]
                }
            ]
        });

        var path = Path.Combine(_tempDir, "noretry_save.xml");
        WatchListXmlParser.Save(config, path);

        var xml = File.ReadAllText(path);
        Assert.DoesNotContain("MaxRetries", xml);
        Assert.DoesNotContain("RetryDelaySeconds", xml);
        Assert.DoesNotContain("RetryBackoff", xml);
    }

    [Fact]
    public void Save_Should_OmitDefaultRetryDelay_When_DelayIsDefault()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "DefaultDelay", Path = @"C:\", Filter = "*.*",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    ExecutionType = ExecutionMode.Sequential,
                    Children =
                    [
                        new ActionConfig
                        {
                            Type = ActionType.RunCommand,
                            Command = "cmd.exe",
                            MaxRetries = 2,
                            RetryDelaySeconds = 10,  // default — should be omitted
                            RetryBackoff = "Exponential",  // default — should be omitted
                        }
                    ]
                }
            ]
        });

        var path = Path.Combine(_tempDir, "default_delay.xml");
        WatchListXmlParser.Save(config, path);

        var xml = File.ReadAllText(path);
        Assert.Contains("MaxRetries", xml);
        Assert.DoesNotContain("RetryDelaySeconds", xml);  // omitted when default
        Assert.DoesNotContain("RetryBackoff", xml);  // omitted when default
    }

    // ?????????????????????????????????????????????????????????????????
    // WatchItem serialize/deserialize round-trip
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void SerializeWatchItem_Should_RoundTripRetry_When_DeserialisedBack()
    {
        var wi = new WatchItemConfig
        {
            Tag = "RetryItem", Path = @"C:\Watch", Filter = "*.*",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    ExecutionType = ExecutionMode.Sequential,
                    Children =
                    [
                        new ActionConfig
                        {
                            Type = ActionType.RunRemoteCommand,
                            Command = "test.bat",
                            MaxRetries = 2,
                            RetryDelaySeconds = 30,
                            RetryBackoff = "Fixed",
                            RetryOnExitCodes = "1,2",
                        }
                    ]
                }
            ]
        };

        var xml = WatchListXmlParser.SerializeWatchItem(wi);
        var deserialized = WatchListXmlParser.DeserializeWatchItem(xml);

        Assert.NotNull(deserialized);
        var action = deserialized.Events[0].Children[0] as ActionConfig;
        Assert.NotNull(action);
        Assert.Equal(2, action.MaxRetries);
        Assert.Equal(30, action.RetryDelaySeconds);
        Assert.Equal("Fixed", action.RetryBackoff);
        Assert.Equal("1,2", action.RetryOnExitCodes);
    }
}
