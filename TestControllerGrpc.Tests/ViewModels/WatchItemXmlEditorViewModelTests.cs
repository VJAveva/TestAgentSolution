using TestControllerGrpc.Core.Preflight;
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

    private sealed class AlwaysExistsFileSystem : IPreflightFileSystem
    {
        public PathAccess CheckFile(string path) => PathAccess.Exists;
        public PathAccess CheckDirectory(string path) => PathAccess.Exists;
        public double? FreeSpaceGb(string path) => 100;
        public string Identity => "test";
    }
}
