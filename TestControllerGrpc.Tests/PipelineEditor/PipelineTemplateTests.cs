using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.PipelineEditor;

public class PipelineTemplateTests
{
    private const string WatchList =
        "<WatchList>\n" +
        "  <Templates>\n" +
        "    <Template ID=\"Prep\">\n" +
        "      <Action Tag=\"Clean\" Type=\"RunCommand\" Command=\"clean.bat\" />\n" +
        "      <Action Tag=\"Copy\" Type=\"RunCommand\" Command=\"copy.bat\" />\n" +
        "    </Template>\n" +
        "  </Templates>\n" +
        "  <WatchItem Tag=\"P\" Path=\"C:\\drop\\\" Filter=\"t.txt\">\n" +
        "    <Event Type=\"Renamed\" ExecutionType=\"Sequential\">\n" +
        "      <Ref TemplateID=\"Prep\" />\n" +
        "      <ActionGroup Tag=\"Install\" ExecutionType=\"Parallel\">\n" +
        "        <Action Tag=\"Setup\" Type=\"RunCommand\" Command=\"setup.exe\" />\n" +
        "      </ActionGroup>\n" +
        "    </Event>\n" +
        "  </WatchItem>\n" +
        "</WatchList>";

    private static WatchListConfig Context() => WatchListXmlParser.DeserializeWatchList(WatchList)!;

    private static PipelineEditorSessionViewModel Session(WatchListConfig context, string? templateBlocked = null)
    {
        var pipeline = new WatchItemXmlEditorViewModel(
            WatchListXmlParser.SerializeWatchItem(context.WatchItems[0]), context, fileSystem: new AlwaysExistsFileSystem());
        return new PipelineEditorSessionViewModel(pipeline, context, fileSystem: new AlwaysExistsFileSystem())
        {
            TemplateEditBlocked = () => templateBlocked,
            Confirm = _ => true,
        };
    }

    private static PipelineStructureNode Find(WatchItemXmlEditorViewModel tab, Func<PipelineStructureNode, bool> match)
    {
        IEnumerable<PipelineStructureNode> All(IEnumerable<PipelineStructureNode> nodes) =>
            nodes.SelectMany(n => All(n.Children).Prepend(n));
        return All(tab.StructureRoots).First(match);
    }

    [Fact]
    public void Ref_Should_ShowTheTemplateStepsReadOnly_When_Expanded()
    {
        var session = Session(Context());
        var call = Find(session.Pipeline, n => n.Kind == NodeKinds.Ref);

        Assert.False(call.IsExpanded);
        Assert.Equal(["Clean", "Copy"], call.Children.Select(c => c.Name));
        Assert.All(call.Children, c => Assert.True(c.IsFromTemplate));
        Assert.Equal([NodeMenuIds.EditTemplate], session.Pipeline.StructureMenuFor(call.Children[1]).Select(i => i.Id));
    }

    [Fact]
    public void EditTemplate_Should_OpenATabWithTheStepSelectedAndTheBanner_When_StartedFromAStep()
    {
        var session = Session(Context());
        var step = Find(session.Pipeline, n => n.Name == "Copy");

        session.Pipeline.RunStructureCommand(NodeMenuIds.EditTemplate, step);

        var tab = session.SelectedTab;
        Assert.Equal("Template: Prep", tab.TabTitle);
        Assert.Equal("t0/c1", tab.SelectedStructureNode?.Path);
        Assert.Contains("used by 1 pipeline: P", tab.Banner);
        Assert.False(tab.IsReadOnly);
    }

    [Fact]
    public void TemplateTab_Should_BeReadOnly_When_TheUserLacksTemplateEdit()
    {
        var session = Session(Context(), templateBlocked: "Administrator only");
        session.OpenTemplate("Prep", null);
        var tab = session.SelectedTab;
        var copy = Find(tab, n => n.Name == "Copy");
        tab.SelectPath(copy.Path);

        Assert.True(tab.IsReadOnly);
        Assert.False(tab.RunStructureCommand(NodeMenuIds.Delete, copy));
        Assert.NotEmpty(tab.SelectedFields);
        Assert.All(tab.SelectedFields, f => Assert.True(f.IsReadOnly));
    }

    [Fact]
    public void Save_Should_WriteTheTemplate_When_ItsUsersStayValid()
    {
        var session = Session(Context());
        session.OpenTemplate("Prep", null);
        session.SelectedTab.XmlText = session.SelectedTab.XmlText.Replace("copy.bat", "copy2.bat");

        session.SaveCommand.Execute(null);

        Assert.True(session.DialogAccepted);
        Assert.Null(session.PipelineResultXml);
        var change = Assert.Single(session.TemplateResults);
        Assert.Equal("Prep", change.OriginalId);
        Assert.Contains("copy2.bat", change.Xml);
    }

    [Fact]
    public void Save_Should_BlockAndNameThePipeline_When_TheTemplateChangeBreaksIt()
    {
        var context = Context();
        var renamed = context.Templates[0];
        var problems = PipelineTemplateEdits.CheckPipelinesUsing(context,
            [("Prep", new TemplateConfig { ID = "Prep2", Children = renamed.Children })], null, null, new AlwaysExistsFileSystem());

        var problem = Assert.Single(problems);
        Assert.Equal("P", problem.Pipeline);
        Assert.Contains("Prep", problem.Message);
    }

    [Fact]
    public void Save_Should_RefuseToOverwrite_When_ThePipelineChangedElsewhere()
    {
        var session = Session(Context());
        var newer = session.Pipeline.XmlText.Replace("t.txt", "u.txt");
        session.LatestSavedXml = _ => newer;
        session.ConfirmReload = _ => true;
        session.Pipeline.XmlText = session.Pipeline.XmlText.Replace("setup.exe", "setup2.exe");

        session.SaveCommand.Execute(null);

        Assert.False(session.DialogAccepted);
        Assert.Equal(newer, session.Pipeline.XmlText);
    }

    [Fact]
    public void CopyForThisPipelineOnly_Should_ReplaceTheRefWithTheSteps_When_Confirmed()
    {
        var session = Session(Context());
        var call = Find(session.Pipeline, n => n.Kind == NodeKinds.Ref);

        Assert.True(session.Pipeline.RunStructureCommand(PipelineEditorMenuIds.CopyTemplateHere, call));

        Assert.DoesNotContain("<Ref", session.Pipeline.XmlText);
        Assert.Contains("Command=\"clean.bat\"", session.Pipeline.XmlText);
        Assert.Contains("Command=\"copy.bat\"", session.Pipeline.XmlText);
    }

    [Fact]
    public void SaveGroupAsTemplate_Should_ReplaceTheGroupWithARefAndOpenANewTab_When_NameIsFree()
    {
        var session = Session(Context());
        var group = Find(session.Pipeline, n => n.Kind == NodeKinds.ActionGroup);

        Assert.True(session.Pipeline.RunStructureCommand(NodeMenuIds.SaveAsTemplate, group, "InstallSteps"));
        Assert.Contains("<Ref TemplateID=\"InstallSteps\" />", session.Pipeline.XmlText);
        Assert.Equal("Template: InstallSteps", session.SelectedTab.TabTitle);

        session.SaveCommand.Execute(null);

        Assert.True(session.DialogAccepted);
        var created = Assert.Single(session.TemplateResults);
        Assert.Null(created.OriginalId);
        Assert.Contains("ExecutionType=\"Parallel\"", created.Xml);
        Assert.False(session.Pipeline.RunStructureCommand(NodeMenuIds.SaveAsTemplate,
            Find(session.Pipeline, n => n.Kind == NodeKinds.Ref && n.Name == "Prep"), "Prep"));
    }

    private sealed class AlwaysExistsFileSystem : IPreflightFileSystem
    {
        public PathAccess CheckFile(string path) => PathAccess.Exists;
        public PathAccess CheckDirectory(string path) => PathAccess.Exists;
        public double? FreeSpaceGb(string path) => 100;
        public string Identity => "test";
    }
}
