using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

public class WatchItemXmlEditorViewModelTests
{
    private const string Original =
        "<WatchItem Tag=\"Old\" Path=\"C:\\\" Filter=\"t.txt\">\n" +
        "  <Event Type=\"Renamed\" ExecutionType=\"Sequential\" />\n" +
        "</WatchItem>";

    private static WatchItemXmlEditorViewModel Create() =>
        new(Original, fileSystem: new AlwaysExistsFileSystem());

    [Fact]
    public void HasUnsavedChanges_Should_BeFalse_When_TextUnchanged()
    {
        Assert.False(Create().HasUnsavedChanges);
    }

    [Fact]
    public void HasUnsavedChanges_Should_BeTrue_When_TextEdited()
    {
        var vm = Create();
        vm.XmlText = Original.Replace("t.txt", "u.txt");
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void HasUnsavedChanges_Should_BeFalse_When_RevertedToOriginal()
    {
        var vm = Create();
        vm.XmlText = Original.Replace("t.txt", "u.txt");
        vm.RevertCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Save_Should_StayOpen_When_TagRenameDeclined()
    {
        var vm = Create();
        (string, string)? asked = null;
        vm.ConfirmTagRename = (o, n) => { asked = (o, n); return false; };
        vm.XmlText = Original.Replace("Tag=\"Old\"", "Tag=\"New\"");

        vm.SaveCommand.Execute(null);

        Assert.Equal(("Old", "New"), asked);
        Assert.False(vm.DialogAccepted);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Save_Should_Accept_When_TagRenameConfirmed()
    {
        var vm = Create();
        vm.ConfirmTagRename = (_, _) => true;
        vm.XmlText = Original.Replace("Tag=\"Old\"", "Tag=\"New\"");

        vm.SaveCommand.Execute(null);

        Assert.True(vm.DialogAccepted);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Save_Should_NotAsk_When_TagUnchanged()
    {
        var vm = Create();
        var asked = false;
        vm.ConfirmTagRename = (_, _) => asked = true;
        vm.XmlText = Original.Replace("t.txt", "u.txt");

        vm.SaveCommand.Execute(null);

        Assert.False(asked);
        Assert.True(vm.DialogAccepted);
    }

    [Fact]
    public async Task RefreshDiagnosticsAsync_Should_NotMoveTheCaret_When_ErrorsFoundWhileTyping()
    {
        var vm = new WatchItemXmlEditorViewModel(Original.Replace("Tag=\"Old\"", "Tag=\"\""),
            fileSystem: new AlwaysExistsFileSystem());
        var navigations = 0;
        vm.NavigateToError += (_, _) => navigations++;

        await vm.RefreshDiagnosticsAsync();

        Assert.NotEmpty(vm.ValidationErrors);
        Assert.Equal(0, navigations);
    }

    [Fact]
    public void Validate_Should_MoveTheCaretToTheFirstError_When_UserAsks()
    {
        var vm = new WatchItemXmlEditorViewModel(Original.Replace("Tag=\"Old\"", "Tag=\"\""),
            fileSystem: new AlwaysExistsFileSystem());
        var navigations = 0;
        vm.NavigateToError += (_, _) => navigations++;

        vm.ValidateCommand.Execute(null);

        Assert.Equal(1, navigations);
    }

    private const string Steps =
        "<WatchItem Tag=\"P\" Path=\"C:\\\" Filter=\"t.txt\">\n" +
        "  <Event Type=\"Renamed\" ExecutionType=\"Sequential\">\n" +
        "    <Action Tag=\"A\" Command=\"a.bat\" />\n" +
        "    <Action Tag=\"B\" Command=\"b.bat\" />\n" +
        "  </Event>\n" +
        "</WatchItem>";

    [Fact]
    public void SyncSelectionToCaret_Should_SelectTheNodeWithoutNavigating_When_CaretMoves()
    {
        var vm = new WatchItemXmlEditorViewModel(Steps, fileSystem: new AlwaysExistsFileSystem());
        var navigations = 0;
        vm.NavigateToOffset += _ => navigations++;

        vm.SyncSelectionToCaret(Steps.IndexOf("b.bat", StringComparison.Ordinal));

        Assert.Equal("e0/c1", vm.SelectedStructureNode?.Path);
        Assert.Contains(vm.SelectedFields, field => field is { Attribute: "Command", Value: "b.bat" });
        Assert.Equal(0, navigations);
    }

    [Fact]
    public void SelectedStructureNode_Should_MoveTheCaretToTheNode_When_PickedInTheTree()
    {
        var vm = new WatchItemXmlEditorViewModel(Steps, fileSystem: new AlwaysExistsFileSystem());
        int? offset = null;
        vm.NavigateToOffset += o => offset = o;

        vm.SelectedStructureNode = vm.StructureRoots[0].Children[0].Children[1];

        Assert.Equal(Steps.IndexOf("<Action Tag=\"B\"", StringComparison.Ordinal), offset);
    }

    [Fact]
    public void Validate_Should_PutTheProblemOnItsNode_When_TagIsEmpty()
    {
        var vm = new WatchItemXmlEditorViewModel(Original.Replace("Tag=\"Old\"", "Tag=\"\""),
            fileSystem: new AlwaysExistsFileSystem());

        vm.ValidateCommand.Execute(null);

        Assert.True(vm.StructureRoots[0].ErrorCount > 0);
        Assert.All(vm.ValidationErrors.Where(e => e.Line == 1), e => Assert.Equal("", e.NodePath));
    }

    [Fact]
    public void StructureMenuFor_Should_OfferNoRunItems_When_NodeIsAnAction()
    {
        var vm = new WatchItemXmlEditorViewModel(Steps, fileSystem: new AlwaysExistsFileSystem());

        var ids = vm.StructureMenuFor(vm.StructureRoots[0].Children[0].Children[1]).Select(i => i.Id).ToList();

        Assert.Contains(TestControllerGrpc.Menus.NodeMenuIds.MoveUp, ids);
        Assert.All(ids, id => Assert.Contains(id, TestControllerGrpc.Menus.NodeMenuProvider.PipelineEditorIds));
        Assert.DoesNotContain(TestControllerGrpc.Menus.NodeMenuIds.ExecuteAction, ids);
    }

    [Fact]
    public void RunStructureCommand_Should_EditTheXmlAndKeepTheNodeSelected_When_MovedUp()
    {
        var vm = new WatchItemXmlEditorViewModel(Steps, fileSystem: new AlwaysExistsFileSystem());

        var changed = vm.RunStructureCommand(TestControllerGrpc.Menus.NodeMenuIds.MoveUp,
            vm.StructureRoots[0].Children[0].Children[1]);

        Assert.True(changed);
        Assert.True(vm.XmlText.IndexOf("Tag=\"B\"", StringComparison.Ordinal) < vm.XmlText.IndexOf("Tag=\"A\"", StringComparison.Ordinal));
        Assert.Equal("B", vm.SelectedStructureNode?.Name);
        Assert.True(vm.HasUnsavedChanges);
    }

    private const string Mail =
        "<WatchItem Tag=\"P\" Path=\"C:\\\" Filter=\"t.txt\">\n" +
        "  <Event Type=\"Renamed\" ExecutionType=\"Sequential\">\n" +
        "    <Action Tag=\"Run\" Type=\"RunRemoteCommand\" AgentName=\"node1\" Command=\"[_Root]\\a.bat\" Password=\"hunter2\" />\n" +
        "    <Action Tag=\"Mail\" Type=\"SendMail\" To=\"qa@x\" Title=\"Done\" />\n" +
        "  </Event>\n" +
        "</WatchItem>";

    private static WatchItemXmlEditorViewModel CreateMail() =>
        new(Mail, fileSystem: new AlwaysExistsFileSystem(),
            agentStatuses: [new("node1", "Online"), new("node2", "Offline")]);

    private static PipelineFieldViewModel Field(WatchItemXmlEditorViewModel vm, string attribute) =>
        vm.SelectedFields.Single(f => f.Attribute == attribute);

    [Fact]
    public void FieldValue_Should_WriteTheAttributeIntoTheXml_When_AgentIsChanged()
    {
        var vm = CreateMail();
        vm.SelectPath("e0/c0");

        Field(vm, "AgentName").Value = "node2";

        Assert.Contains("AgentName=\"node2\"", vm.XmlText);
        Assert.Equal("e0/c0", vm.SelectedStructureNode?.Path);
        Assert.Equal("Offline", vm.SelectedStructureNode?.AgentStatus);
    }

    [Fact]
    public void Fields_Should_FollowTheXml_When_TheXmlIsEdited()
    {
        var vm = CreateMail();
        vm.SelectPath("e0/c1");

        vm.XmlText = Mail.Replace("Title=\"Done\"", "Title=\"Finished\"");
        vm.RefreshStructure();

        Assert.Equal("Finished", Field(vm, "Title").Value);
    }

    [Fact]
    public void MailBody_Should_RoundTripLineBreaks_When_EditedInTheForm()
    {
        var vm = CreateMail();
        vm.SelectPath("e0/c1");

        Field(vm, "Body").Value = "Line one\r\nLine two";

        Assert.Contains("Body=\"Line one&#xA;Line two\"", vm.XmlText);
        Assert.Equal("Line one\nLine two", Field(vm, "Body").Value);
    }

    [Fact]
    public void Fields_Should_NeverShowSecrets_When_ActionHasAPassword()
    {
        var vm = CreateMail();
        vm.SelectPath("e0/c0");

        var password = Field(vm, "Password");
        Assert.True(password.IsReadOnly);
        Assert.DoesNotContain(vm.SelectedFields, f => f.Value?.Contains("hunter2") == true);
        Assert.Single(Field(vm, "Command").Tokens);
    }

    [Fact]
    public void ErrorCount_Should_IncludeChildren_When_ParentIsCollapsed()
    {
        var vm = new WatchItemXmlEditorViewModel(Steps.Replace("Command=\"b.bat\"", "Command=\"\""),
            fileSystem: new AlwaysExistsFileSystem());
        vm.ValidateCommand.Execute(null);
        var evt = vm.StructureRoots[0].Children[0];
        var own = evt.ErrorCount;

        evt.IsExpanded = false;

        Assert.True(evt.SubtreeErrorCount > own);
        Assert.Equal(evt.SubtreeErrorCount, evt.ErrorCount);
    }

    private sealed class AlwaysExistsFileSystem : IPreflightFileSystem
    {
        public PathAccess CheckFile(string path) => PathAccess.Exists;
        public PathAccess CheckDirectory(string path) => PathAccess.Exists;
        public double? FreeSpaceGb(string path) => 100;
        public string Identity => "test";
    }
}
