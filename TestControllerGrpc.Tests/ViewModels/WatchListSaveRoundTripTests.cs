using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Opening a WatchList and previewing resolved tokens must never change what gets written back.
///
/// The save path used to call a helper that resolved every <c>AgentName</c> through the shared
/// token dictionary and assigned the result onto the model, so saving baked a concrete agent name
/// into WatchList.xml in place of the token. With one flat dictionary shared by all pipelines, the
/// value baked in could be another pipeline's agent - a permanent, silent edit to the config.
/// </summary>
[Collection("TokenState")]
public class WatchListSaveRoundTripTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():N}");

    private readonly string _file;

    private const string Xml = """
    <?xml version="1.0" encoding="utf-8"?>
    <WatchList>
      <WatchItem Tag="Sanity 5 Nodes" Path="C:\Triggers\" Filter="sanity.txt">
        <Event Type="Renamed" ExecutionType="Sequential">
          <Initialize Tag="Init" ParameterFile="C:\Params\pipeline-config.json" Profile="Sanity" />
          <ActionGroup Tag="Install" ExecutionType="Sequential">
            <Action Type="RunRemoteCommand" Tag="Install build" AgentName="[_Agent1]" Command="C:\tools\install.bat" Parameters="[_BuildNumber]" />
          </ActionGroup>
          <Action Type="SendMail" Tag="Notify" From="ci@corp.com" To="[_EmailCheck]" Title="Done - [_BuildNumber]" Body="[INFO] finished" />
        </Event>
      </WatchItem>
    </WatchList>
    """;

    public WatchListSaveRoundTripTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "WatchList.xml");
        File.WriteAllText(_file, Xml);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        TreeNodeViewModel.ClearTokenScopes();
        GC.SuppressFinalize(this);
    }

    /// <summary>Mirrors MainViewModel.WriteBackAll, which runs immediately before every save.</summary>
    private static void WriteBackAll(TreeNodeViewModel node)
    {
        node.ApplyToModel();
        foreach (var child in node.Children) WriteBackAll(child);
    }

    private static void Preview(WatchListConfig config)
    {
        // What opening the file does: populate that pipeline's token scope, build the tree, render
        // resolved text for display.
        var scope = TreeNodeViewModel.TokensFor("Sanity 5 Nodes");
        scope["_Agent1"] = "jvgr1";
        scope["Agent1"] = "jvgr1";
        scope["_BuildNumber"] = "OAK_main_20261003.4";
        scope["BuildNumber"] = "OAK_main_20261003.4";
        scope["_EmailCheck"] = "sanity-team@corp.com";
        scope["EmailCheck"] = "sanity-team@corp.com";

        var root = TreeNodeViewModel.FromWatchList(config);
        root.RefreshResolvedTextRecursive();
        WriteBackAll(root);
    }

    [Fact]
    public void Save_Should_KeepRawTokens_When_FileWasOpenedAndPreviewed()
    {
        var config = WatchListXmlParser.Load(_file);
        Preview(config);
        WatchListXmlParser.Save(config, _file);

        var saved = File.ReadAllText(_file);

        Assert.Contains("[_Agent1]", saved);
        Assert.Contains("[_BuildNumber]", saved);
        Assert.Contains("[_EmailCheck]", saved);
        Assert.DoesNotContain("jvgr1", saved);
        Assert.DoesNotContain("OAK_main_20261003.4", saved);
        Assert.DoesNotContain("sanity-team@corp.com", saved);
    }

    [Fact]
    public void Save_Should_LeaveTheModelUntouched_When_PreviewResolvedTokens()
    {
        var config = WatchListXmlParser.Load(_file);
        Preview(config);

        var action = config.WatchItems[0].Events[0].Children
            .OfType<ActionGroupConfig>().Single()
            .Children.OfType<ActionConfig>().Single();

        Assert.Equal("[_Agent1]", action.AgentName);
        Assert.Equal("[_BuildNumber]", action.Parameters);
    }

    [Fact]
    public void Save_Should_PreserveTheInitializeProfile_When_RoundTripped()
    {
        var config = WatchListXmlParser.Load(_file);
        Preview(config);
        WatchListXmlParser.Save(config, _file);

        var reloaded = WatchListXmlParser.Load(_file);
        var init = reloaded.WatchItems[0].Events[0].Children.OfType<InitializeConfig>().Single();

        Assert.Equal("Sanity", init.Profile);
        Assert.Equal(@"C:\Params\pipeline-config.json", init.ParameterFile);
    }

    /// <summary>
    /// Save twice: the second write must be byte-identical to the first. A resolve-on-save would
    /// keep rewriting the file and the config would drift every time anyone opened it.
    /// </summary>
    [Fact]
    public void Save_Should_BeIdempotent_When_OpenedAndSavedTwice()
    {
        var first = WatchListXmlParser.Load(_file);
        Preview(first);
        WatchListXmlParser.Save(first, _file);
        var afterFirst = File.ReadAllBytes(_file);

        TreeNodeViewModel.ClearTokenScopes();

        var second = WatchListXmlParser.Load(_file);
        Preview(second);
        WatchListXmlParser.Save(second, _file);
        var afterSecond = File.ReadAllBytes(_file);

        Assert.Equal(afterFirst, afterSecond);
    }
}
