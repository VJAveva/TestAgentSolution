using ICSharpCode.AvalonEdit.Document;

namespace TestControllerGrpc.Services;

public static class XmlEditorQuickFixApplier
{
    public static void ReplaceAsSingleUndo(TextDocument document, string replacement)
        => ReplaceRangeAsSingleUndo(document, 0, document.TextLength, replacement);

    public static void ReplaceRangeAsSingleUndo(
        TextDocument document, int offset, int length, string replacement)
    {
        var undoStack = document.UndoStack;
        undoStack.StartUndoGroup();
        try
        {
            document.Replace(offset, length, replacement);
        }
        finally
        {
            undoStack.EndUndoGroup();
        }
    }
}
