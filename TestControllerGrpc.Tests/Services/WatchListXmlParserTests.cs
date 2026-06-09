using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class WatchListXmlParserTests : IDisposable
{
    private readonly string _tempDir;

    public WatchListXmlParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"XmlParserTests_{Guid.NewGuid():N}");
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

    // ???????????????????????????????????????????????????????????????????
    // Load
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void Load_Should_ParseWatchItems_When_ValidXml()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Build1" Path="C:\Triggers" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunCommand" Command="cmd.exe" Parameters="/c echo hello" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("test.xml", xml);

        var config = WatchListXmlParser.Load(path);

        Assert.Single(config.WatchItems);
        Assert.Equal("Build1", config.WatchItems[0].Tag);
        Assert.Equal(@"C:\Triggers", config.WatchItems[0].Path);
        Assert.Equal("*.txt", config.WatchItems[0].Filter);
        Assert.Single(config.WatchItems[0].Events);
        Assert.Equal("Renamed", config.WatchItems[0].Events[0].Type);
        Assert.Equal(ExecutionMode.Sequential, config.WatchItems[0].Events[0].ExecutionType);
    }

    [Fact]
    public void Load_Should_ParseTemplates_When_TemplatesSectionPresent()
    {
        var xml = """
            <WatchList>
              <Templates>
                <Template ID="MyTemplate">
                  <Action Type="RunCommand" Command="cmd.exe" />
                </Template>
              </Templates>
            </WatchList>
            """;
        var path = WriteTempXml("templates.xml", xml);

        var config = WatchListXmlParser.Load(path);

        Assert.Single(config.Templates);
        Assert.Equal("MyTemplate", config.Templates[0].ID);
        Assert.Single(config.Templates[0].Children);
    }

    [Fact]
    public void Load_Should_ParseActionAttributes_When_FullySpecified()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="Test" Path="C:\" Filter="*.*">
                <Event Type="Created" ExecutionType="Parallel">
                  <Action Type="RunRemoteCommand" AgentName="Agent1" Command="install.bat"
                          Parameters="/silent" Timeout="600" PollInterval="30"
                          FailAndContinue="true" IsReboot="true" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("attrs.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var action = config.WatchItems[0].Events[0].Children[0] as ActionConfig;

        Assert.NotNull(action);
        Assert.Equal(ActionType.RunRemoteCommand, action.Type);
        Assert.Equal("Agent1", action.AgentName);
        Assert.Equal("install.bat", action.Command);
        Assert.Equal("/silent", action.Parameters);
        Assert.Equal(600, action.Timeout);
        Assert.Equal(30, action.PollInterval);
        Assert.True(action.FailAndContinue);
        Assert.True(action.IsReboot);
    }

    [Fact]
    public void Load_Should_ParseNestedActionGroups_When_GroupsAreNested()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="Nested" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <ActionGroup Tag="Outer" ExecutionType="Sequential">
                    <ActionGroup Tag="Inner" ExecutionType="Parallel" FailAndContinue="true">
                      <Action Type="RunCommand" Command="cmd.exe" />
                    </ActionGroup>
                  </ActionGroup>
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("nested.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var outer = config.WatchItems[0].Events[0].Children[0] as ActionGroupConfig;
        Assert.NotNull(outer);
        Assert.Equal("Outer", outer.Tag);

        var inner = outer.Children[0] as ActionGroupConfig;
        Assert.NotNull(inner);
        Assert.Equal("Inner", inner.Tag);
        Assert.Equal(ExecutionMode.Parallel, inner.ExecutionType);
        Assert.True(inner.FailAndContinue);
    }

    [Fact]
    public void Load_Should_ParseInitialize_When_InitNodePresent()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="Init" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Initialize Tag="Params" ParameterFile="C:\params.txt" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("init.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var init = config.WatchItems[0].Events[0].Children[0] as InitializeConfig;

        Assert.NotNull(init);
        Assert.Equal("Params", init.Tag);
        Assert.Equal(@"C:\params.txt", init.ParameterFile);
    }

    [Fact]
    public void Load_Should_ParseRef_When_RefNodePresent()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="RefTest" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Ref TemplateID="UC152TCS" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("ref.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var refNode = config.WatchItems[0].Events[0].Children[0] as RefConfig;

        Assert.NotNull(refNode);
        Assert.Equal("UC152TCS", refNode.TemplateID);
    }

    [Fact]
    public void Load_Should_DefaultFilter_When_FilterAttributeMissing()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="NoFilter" Path="C:\"></WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("nofilter.xml", xml);

        var config = WatchListXmlParser.Load(path);
        Assert.Equal("*.*", config.WatchItems[0].Filter);
    }

    [Fact]
    public void Load_Should_Throw_When_FileDoesNotExist()
    {
        Assert.ThrowsAny<Exception>(() =>
            WatchListXmlParser.Load(@"C:\definitely\not\a\file.xml"));
    }

    [Fact]
    public void Load_Should_SetFilePath_When_Loading()
    {
        var xml = "<WatchList></WatchList>";
        var path = WriteTempXml("filepath.xml", xml);

        var config = WatchListXmlParser.Load(path);
        Assert.Equal(path, config.FilePath);
    }

    // ???????????????????????????????????????????????????????????????????
    // Save + RoundTrip
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void Save_Should_RoundTrip_When_LoadedThenSaved()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "RT",
            Path = @"C:\Triggers",
            Filter = "*.txt",
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
                            AgentName = "Agent1",
                            Command = "install.bat",
                            Parameters = "/s",
                            Timeout = 600,
                            FailAndContinue = true,
                        },
                        new InitializeConfig { Tag = "Init", ParameterFile = @"C:\p.txt" },
                        new RefConfig { TemplateID = "T1" },
                    ]
                }
            ]
        });
        config.Templates.Add(new TemplateConfig
        {
            ID = "T1",
            Children = [new ActionConfig { Type = ActionType.RunCommand, Command = "echo" }]
        });

        var path = Path.Combine(_tempDir, "roundtrip.xml");
        WatchListXmlParser.Save(config, path);
        var reloaded = WatchListXmlParser.Load(path);

        Assert.Single(reloaded.WatchItems);
        Assert.Equal("RT", reloaded.WatchItems[0].Tag);
        Assert.Single(reloaded.Templates);
        Assert.Equal("T1", reloaded.Templates[0].ID);

        var children = reloaded.WatchItems[0].Events[0].Children;
        Assert.Equal(3, children.Count);
        Assert.IsType<ActionConfig>(children[0]);
        Assert.IsType<InitializeConfig>(children[1]);
        Assert.IsType<RefConfig>(children[2]);
    }

    [Fact]
    public void Save_Should_EmitEmptyWatchList_When_NoItems()
    {
        var config = new WatchListConfig();
        var path = Path.Combine(_tempDir, "empty.xml");

        WatchListXmlParser.Save(config, path);
        var reloaded = WatchListXmlParser.Load(path);

        Assert.Empty(reloaded.WatchItems);
        Assert.Empty(reloaded.Templates);
    }

    // ???????????????????????????????????????????????????????????????????
    // SerializeWatchItem / DeserializeWatchItem
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SerializeWatchItem_Should_RoundTrip_When_DeserialisedBack()
    {
        var wi = new WatchItemConfig
        {
            Tag = "TestItem",
            Path = @"C:\Watch",
            Filter = "*.log",
            Events =
            [
                new EventConfig
                {
                    Type = "Created",
                    ExecutionType = ExecutionMode.Parallel,
                    Children =
                    [
                        new ActionConfig { Type = ActionType.SendMail, From = "a@b.com", To = "c@d.com", Title = "Test" }
                    ]
                }
            ]
        };

        var xml = WatchListXmlParser.SerializeWatchItem(wi);
        var deserialized = WatchListXmlParser.DeserializeWatchItem(xml);

        Assert.NotNull(deserialized);
        Assert.Equal("TestItem", deserialized.Tag);
        Assert.Equal(@"C:\Watch", deserialized.Path);
        Assert.Equal("*.log", deserialized.Filter);
        Assert.Single(deserialized.Events);
    }

    [Fact]
    public void DeserializeWatchItem_Should_ReturnNull_When_RootIsNotWatchItem()
    {
        var result = WatchListXmlParser.DeserializeWatchItem("<NotAWatchItem />");
        Assert.Null(result);
    }

    // ???????????????????????????????????????????????????????????????????
    // SerializeTemplateList / DeserializeTemplateList
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SerializeTemplateList_Should_RoundTrip_When_DeserialisedBack()
    {
        var templates = new List<TemplateConfig>
        {
            new() { ID = "T1", Children = [new ActionConfig { Command = "echo" }] },
            new() { ID = "T2", Children = [] },
        };

        var xml = WatchListXmlParser.SerializeTemplateList(templates);
        var deserialized = WatchListXmlParser.DeserializeTemplateList(xml);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("T1", deserialized[0].ID);
        Assert.Equal("T2", deserialized[1].ID);
    }

    [Fact]
    public void DeserializeTemplateList_Should_ReturnNull_When_RootIsNotTemplates()
    {
        var result = WatchListXmlParser.DeserializeTemplateList("<NotTemplates />");
        Assert.Null(result);
    }

    // ???????????????????????????????????????????????????????????????????
    // DeserializeWatchList (full inline)
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void DeserializeWatchList_Should_ReturnNull_When_RootIsNotWatchList()
    {
        var result = WatchListXmlParser.DeserializeWatchList("<NotWatchList />");
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeWatchList_Should_ParseFully_When_ValidXml()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="A" Path="C:\" Filter="*.*" />
              <Templates>
                <Template ID="T1">
                  <Action Type="RunCommand" Command="echo" />
                </Template>
              </Templates>
            </WatchList>
            """;

        var config = WatchListXmlParser.DeserializeWatchList(xml);

        Assert.NotNull(config);
        Assert.Single(config.WatchItems);
        Assert.Single(config.Templates);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Action Tag XML roundtrip
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_Should_ParseActionTag_When_TagAttributePresent()
    {
        var xml = """
            <WatchList>
              <WatchItem Tag="Test" Path="C:\" Filter="*.*">
                <Event Type="Renamed" ExecutionType="Sequential">
                  <Action Type="RunRemoteCommand" AgentName="Agent1"
                          Command="install.bat" Tag="Install WSP" Order="Step3" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("tag_attr.xml", xml);

        var config = WatchListXmlParser.Load(path);
        var action = config.WatchItems[0].Events[0].Children[0] as ActionConfig;

        Assert.NotNull(action);
        Assert.Equal("Install WSP", action.Tag);
        Assert.Equal("Step3", action.Order);
        Assert.Equal("Install WSP", action.ResolvedTag);
    }

    [Fact]
    public void Save_Should_RoundTripActionTag_When_TagIsSet()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "TagRT",
            Path = @"C:\Triggers",
            Filter = "*.txt",
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
                            AgentName = "Agent1",
                            Command = "install.bat",
                            Tag = "Install WSP",
                            Order = "Step3",
                        },
                    ]
                }
            ]
        });

        var path = Path.Combine(_tempDir, "tag_roundtrip.xml");
        WatchListXmlParser.Save(config, path);
        var reloaded = WatchListXmlParser.Load(path);

        var action = reloaded.WatchItems[0].Events[0].Children[0] as ActionConfig;
        Assert.NotNull(action);
        Assert.Equal("Install WSP", action.Tag);
        Assert.Equal("Step3", action.Order);
    }

    [Fact]
    public void Save_Should_OmitTagAttribute_When_TagIsEmpty()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "NoTag",
            Path = @"C:\Triggers",
            Filter = "*.txt",
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
                            Command = "echo hello",
                        },
                    ]
                }
            ]
        });

        var path = Path.Combine(_tempDir, "no_tag.xml");
        WatchListXmlParser.Save(config, path);
        var xml = File.ReadAllText(path);

        // Tag="" should not appear in the XML (AddIfNotEmpty skips empty strings)
        Assert.DoesNotContain("Tag=\"\"", xml);
    }

    [Fact]
    public void Load_Should_ParseIsEnabled_When_AttributePresent()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <WatchList>
              <WatchItem Tag="Enabled" Path="C:\A" Filter="*.txt">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
              <WatchItem Tag="Disabled" Path="C:\B" Filter="*.txt" IsEnabled="false">
                <Event Type="Renamed" ExecutionType="Sequential" />
              </WatchItem>
            </WatchList>
            """;
        var path = WriteTempXml("is_enabled.xml", xml);

        var config = WatchListXmlParser.Load(path);

        Assert.True(config.WatchItems[0].IsEnabled);
        Assert.False(config.WatchItems[1].IsEnabled);
    }

    [Fact]
    public void Save_Should_RoundTripIsEnabled_When_False()
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "Active", Path = @"C:\A", Filter = "*.txt", IsEnabled = true,
            Events = [new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential }]
        });
        config.WatchItems.Add(new WatchItemConfig
        {
            Tag = "Inactive", Path = @"C:\B", Filter = "*.txt", IsEnabled = false,
            Events = [new EventConfig { Type = "Renamed", ExecutionType = ExecutionMode.Sequential }]
        });

        var path = Path.Combine(_tempDir, "is_enabled_rt.xml");
        WatchListXmlParser.Save(config, path);

        var reloaded = WatchListXmlParser.Load(path);
        Assert.True(reloaded.WatchItems[0].IsEnabled);
        Assert.False(reloaded.WatchItems[1].IsEnabled);
    }
}
