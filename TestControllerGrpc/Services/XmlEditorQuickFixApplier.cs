using ICSharpCode.AvalonEdit.Document;

namespace TestControllerGrpc.Services;

public static class XmlEditorQuickFixApplier
{
    /// <summary>
    /// Replaces the document with <paramref name="replacement"/> as one undo step, touching only the
    /// span that differs so the caret, scroll position and folds outside it stay where they were.
    /// </summary>
    public static void ReplaceAsSingleUndo(TextDocument document, string replacement)
    {
        var current = document.Text;
        var max = Math.Min(current.Length, replacement.Length);
        var prefix = 0;
        while (prefix < max && current[prefix] == replacement[prefix]) prefix++;
        if (prefix == current.Length && prefix == replacement.Length) return;

        var suffix = 0;
        while (suffix < max - prefix
               && current[current.Length - 1 - suffix] == replacement[replacement.Length - 1 - suffix])
            suffix++;

        ReplaceRangeAsSingleUndo(document, prefix, current.Length - prefix - suffix,
            replacement.Substring(prefix, replacement.Length - prefix - suffix));
    }

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
