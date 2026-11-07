using ICSharpCode.AvalonEdit.Document;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class XmlEditorTypingTests
{
    [Theory]
    [InlineData("<Action Type=\"", "Type")]
    [InlineData("<Action\n    Command = \"", "Command")]
    public void AttributeOpenedByQuote_Should_NameTheAttribute_When_QuoteOpensAValue(string before, string expected)
    {
        Assert.Equal(expected, XmlEditorTyping.AttributeOpenedByQuote(before));
    }

    [Theory]
    [InlineData("<Action Type=\"RunCommand\"")]                 // closing quote
    [InlineData("<Action Parameters=\"/c echo \"")]             // quote typed inside a value
    [InlineData("<Event Type=\"Renamed\">text \"")]            // text content, not a tag
    public void AttributeOpenedByQuote_Should_BeNull_When_QuoteDoesNotOpenAValue(string before)
    {
        Assert.Null(XmlEditorTyping.AttributeOpenedByQuote(before));
    }

    [Theory]
    [InlineData("<Action ", "Action")]
    [InlineData("<ActionGroup Tag=\"x\"\n    ", "ActionGroup")]
    public void CurrentElementName_Should_NameTheTag_When_CaretIsBetweenAttributes(string before, string expected)
    {
        Assert.Equal(expected, XmlEditorTyping.CurrentElementName(before));
    }

    [Theory]
    [InlineData("<Action Command=\"C:\\Program ")]              // space inside a value
    [InlineData("<Action Type=\"x\" />\n  ")]                  // after a closed tag
    [InlineData("</Event ")]                                     // end tag
    public void CurrentElementName_Should_BeNull_When_NotBetweenAttributes(string before)
    {
        Assert.Null(XmlEditorTyping.CurrentElementName(before));
    }

    [Fact]
    public void AutoClose_Should_SelfCloseLeaf_When_ActionStartTagEnds()
    {
        Assert.Equal(("Action", true), XmlEditorTyping.AutoClose("<Action Type=\"RunCommand\">", ""));
    }

    [Fact]
    public void AutoClose_Should_InsertEndTag_When_ContainerStartTagEnds()
    {
        Assert.Equal(("ActionGroup", false), XmlEditorTyping.AutoClose("<ActionGroup Tag=\"g\">", "\n"));
    }

    [Theory]
    [InlineData("<Action Parameters=\"a >", "")]                 // '>' typed inside a value
    [InlineData("<ActionGroup Tag=\"g\">", "</ActionGroup>")]    // already closed
    [InlineData("<Ref TemplateID=\"t\" />", "")]                 // self-closed by the user
    [InlineData("</ActionGroup>", "")]                           // end tag
    public void AutoClose_Should_DoNothing_When_InsertWouldCorruptOrDuplicate(string before, string after)
    {
        Assert.Null(XmlEditorTyping.AutoClose(before, after));
    }

    [Theory]
    [InlineData(' ', false)]
    [InlineData('"', false)]
    [InlineData('\\', false)]
    [InlineData('a', true)]
    [InlineData('_', true)]
    public void KeepsCompletionOpen_Should_OnlyAllowNameCharacters(char typed, bool expected)
    {
        Assert.Equal(expected, XmlEditorTyping.KeepsCompletionOpen(typed));
    }
}

public class XmlEditorQuickFixApplierTests
{
    [Fact]
    public void ReplaceAsSingleUndo_Should_KeepAnOffsetOutsideTheChange_When_DocumentReplaced()
    {
        var doc = new TextDocument("<A x=\"1\" />\n<B y=\"2\" />");
        var caret = doc.CreateAnchor(doc.Text.IndexOf("<B", StringComparison.Ordinal));

        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(doc, "<A x=\"100\" />\n<B y=\"2\" />");

        Assert.Equal("<A x=\"100\" />\n<B y=\"2\" />", doc.Text);
        Assert.Equal(doc.Text.IndexOf("<B", StringComparison.Ordinal), caret.Offset);
    }

    [Fact]
    public void ReplaceAsSingleUndo_Should_RestoreOriginalInOneUndo_When_Undone()
    {
        var doc = new TextDocument("<A x=\"1\" />");
        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(doc, "<A x=\"2\" y=\"3\" />");

        doc.UndoStack.Undo();

        Assert.Equal("<A x=\"1\" />", doc.Text);
        Assert.False(doc.UndoStack.CanUndo);
    }

    [Fact]
    public void ReplaceAsSingleUndo_Should_DoNothing_When_TextUnchanged()
    {
        var doc = new TextDocument("<A />");
        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(doc, "<A />");
        Assert.False(doc.UndoStack.CanUndo);
    }
}
