namespace TestControllerGrpc.Services;

/// <summary>
/// Decides what the XML editors may do as the user types. Pure string logic over the text before
/// the caret, so the rules are testable and both editor windows share them.
/// </summary>
public static class XmlEditorTyping
{
    /// <summary>How much text before the caret is enough to see the enclosing start tag.</summary>
    public const int LookBehind = 4000;

    private static readonly HashSet<string> SelfClosingElements =
        new(StringComparer.OrdinalIgnoreCase) { "Action", "Initialize", "Ref" };

    /// <summary>Text of the start tag the caret is in, from its '&lt;'; null when the caret is not in a start tag.</summary>
    public static string? OpenStartTag(string textBeforeCaret)
    {
        var lt = textBeforeCaret.LastIndexOf('<');
        if (lt < 0) return null;
        var segment = textBeforeCaret[lt..];
        if (segment.Length > 1 && segment[1] is '/' or '!' or '?') return null;

        var inQuote = false;
        foreach (var c in segment)
        {
            if (c == '"') inQuote = !inQuote;
            else if (c == '>' && !inQuote) return null;
        }
        return segment;
    }

    public static bool IsInsideAttributeValue(string textBeforeCaret) =>
        OpenStartTag(textBeforeCaret) is { } tag && tag.Count(c => c == '"') % 2 == 1;

    /// <summary>Element name of the start tag being typed, only when the caret is between attributes.</summary>
    public static string? CurrentElementName(string textBeforeCaret)
    {
        var tag = OpenStartTag(textBeforeCaret);
        if (tag is null || tag.Count(c => c == '"') % 2 == 1) return null;
        var name = new string(tag.Skip(1).TakeWhile(IsNameChar).ToArray());
        return name.Length > 0 ? name : null;
    }

    /// <summary>The attribute whose value was just opened by the quote before the caret; null for a closing quote.</summary>
    public static string? AttributeOpenedByQuote(string textBeforeCaret)
    {
        if (!textBeforeCaret.EndsWith('"') || !IsInsideAttributeValue(textBeforeCaret)) return null;
        var beforeQuote = textBeforeCaret[..^1].TrimEnd();
        if (!beforeQuote.EndsWith('=')) return null;
        var beforeEquals = beforeQuote[..^1].TrimEnd();
        var name = new string(beforeEquals.Reverse().TakeWhile(IsNameChar).Reverse().ToArray());
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// What to insert after a '&gt;' the user just typed: " />" handling for leaf elements, a closing
    /// tag for containers, or null when the '&gt;' is inside a value, ends an end tag, or is already closed.
    /// </summary>
    public static (string Name, bool SelfClose)? AutoClose(string textBeforeCaret, string textAfterCaret)
    {
        if (!textBeforeCaret.EndsWith('>')) return null;
        var withoutGt = textBeforeCaret[..^1];
        if (withoutGt.TrimEnd().EndsWith('/')) return null;

        var name = CurrentElementName(withoutGt);
        if (name is null) return null;
        if (textAfterCaret.TrimStart().StartsWith($"</{name}", StringComparison.Ordinal)) return null;
        return (name, SelfClosingElements.Contains(name));
    }

    /// <summary>A completion list commits on Enter/Tab only; other punctuation must not insert an item.</summary>
    public static bool KeepsCompletionOpen(char typed) => IsNameChar(typed);

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';
}
