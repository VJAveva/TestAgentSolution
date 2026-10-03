using System.IO;
using System.Xml.Linq;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Guards the editable-ComboBox trap that corrupted AgentName on 2026-10-02.
///
/// An editable ComboBox has TWO text surfaces: the dropdown item and the edit box. When an item is picked,
/// WPF copies the item's PRIMARY TEXT into the edit box, and the primary text comes from
/// <c>TextSearch.TextPath</c> if present, otherwise from <c>DisplayMemberPath</c>. So an editable ComboBox
/// whose <c>Text</c> is bound to a model property and whose <c>DisplayMemberPath</c> points at a DECORATED
/// string will write that decoration into the model.
///
/// That is exactly what happened: <c>DisplayMemberPath="Display"</c> where
/// <c>Display =&gt; $"{Name}  ({Status})"</c> persisted <c>"jvgr2  (free)"</c> as the agent name, and the
/// dispatcher then reported <c>Agent 'jvgr2  (free)' not registered</c>. <c>SelectedValuePath</c> did not
/// help because nothing was bound to <c>SelectedValue</c>.
/// </summary>
public sealed class EditableComboBoxBindingTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> AllXaml() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EditableComboBox_Should_NotBindTextWhileUsingDisplayMemberPath()
    {
        var offenders = new List<string>();

        foreach (var file in AllXaml())
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var cb in doc.Descendants(Presentation + "ComboBox"))
            {
                if ((string?)cb.Attribute("IsEditable") != "True") continue;

                var text = (string?)cb.Attribute("Text");
                if (text is null || !text.Contains("{Binding", StringComparison.Ordinal)) continue;

                var display = (string?)cb.Attribute("DisplayMemberPath");
                if (display is null) continue;

                // TextSearch.TextPath overrides DisplayMemberPath for the edit box, so it makes this safe.
                var textPath = (string?)cb.Attribute(XName.Get("TextPath", "clr-namespace:System.Windows.Controls;assembly=PresentationFramework"))
                               ?? (string?)cb.Attribute("TextSearch.TextPath");
                if (textPath is not null) continue;

                offenders.Add($"  {Path.GetFileName(file)}: Text={text} DisplayMemberPath=\"{display}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "An editable ComboBox binds Text to a model property while DisplayMemberPath supplies the edit-box " +
            "text. Picking an item will write the DISPLAY string into the model. Use TextSearch.TextPath for the " +
            "raw value and an ItemTemplate for the decorated display:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AgentChoice_Display_Should_DifferFromName_SoTheBindingGuardMatters()
    {
        var choice = new TestControllerGrpc.ViewModels.AgentChoice("jvgr2", "free");

        Assert.Equal("jvgr2", choice.Name);
        Assert.NotEqual(choice.Name, choice.Display);
        Assert.Equal("jvgr2  (free)", choice.Display);
    }
}
