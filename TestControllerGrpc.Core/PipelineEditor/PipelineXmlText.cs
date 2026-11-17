using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>One attribute of a start tag: decoded value plus the exact character ranges in the editor text.</summary>
/// <param name="Start">Offset of the attribute name.</param>
/// <param name="End">Offset just past the closing quote.</param>
public sealed record XmlAttributeSpan(string Name, string Value, int Start, int End, int ValueStart, int ValueEnd, char Quote);

/// <summary>
/// Text-level edits on well-formed editor XML. Only the characters of the edited node change, so formatting,
/// comments, line endings and <c>&lt;A/&gt;</c> versus <c>&lt;A /&gt;</c> elsewhere stay exactly as typed.
/// </summary>
public static class PipelineXmlText
{
    /// <summary>Offset past the '&gt;' that ends the tag starting at <paramref name="start"/>; quoted values may hold '&gt;'.</summary>
    public static int StartTagEnd(string xml, int start)
    {
        char? quote = null;
        for (var i = start; i < xml.Length; i++)
        {
            var c = xml[i];
            if (quote is not null) { if (c == quote) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return xml.Length;
    }

    public static IReadOnlyList<XmlAttributeSpan> Attributes(string xml, int elementStart)
    {
        var result = new List<XmlAttributeSpan>();
        var end = StartTagEnd(xml, elementStart);
        var i = elementStart + 1;
        while (i < end && !IsNameEnd(xml[i])) i++;
        while (i < end)
        {
            while (i < end && char.IsWhiteSpace(xml[i])) i++;
            if (i >= end || xml[i] is '/' or '>') break;
            var nameStart = i;
            while (i < end && xml[i] != '=' && !char.IsWhiteSpace(xml[i])) i++;
            var name = xml[nameStart..i];
            while (i < end && xml[i] != '"' && xml[i] != '\'') i++;
            if (i >= end) break;
            var quote = xml[i];
            var valueStart = i + 1;
            var valueEnd = xml.IndexOf(quote, valueStart);
            if (valueEnd < 0 || valueEnd >= end) break;
            result.Add(new XmlAttributeSpan(name, Decode(xml[valueStart..valueEnd], quote),
                nameStart, valueEnd + 1, valueStart, valueEnd, quote));
            i = valueEnd + 1;
        }
        return result;
    }

    /// <summary>Sets, adds or (with a null value) removes one attribute of the element starting at <paramref name="elementStart"/>.</summary>
    public static string SetAttribute(string xml, int elementStart, string name, string? value)
    {
        var existing = Attributes(xml, elementStart).FirstOrDefault(a => a.Name == name);
        if (existing is not null)
        {
            if (value is null)
            {
                var from = existing.Start;
                while (from > elementStart && char.IsWhiteSpace(xml[from - 1])) from--;
                return xml.Remove(from, existing.End - from);
            }
            if (existing.Value == value) return xml;
            return xml[..existing.ValueStart] + Escape(value, existing.Quote) + xml[existing.ValueEnd..];
        }
        if (value is null) return xml;

        var close = StartTagEnd(xml, elementStart) - 1;
        if (close > elementStart && xml[close - 1] == '/') close--;
        while (close > elementStart && char.IsWhiteSpace(xml[close - 1])) close--;
        return xml.Insert(close, $" {name}=\"{Escape(value, '"')}\"");
    }

    /// <summary>
    /// The element's whole lines (indent and line break included) when it sits alone on them;
    /// otherwise just the element. Removing this range leaves no blank line behind.
    /// </summary>
    public static (int Start, int End) LineBlock(string xml, int start, int end)
    {
        var lineStart = start == 0 ? 0 : xml.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = xml.IndexOf('\n', end);
        var after = lineEnd < 0 ? xml[end..] : xml[end..lineEnd];
        return string.IsNullOrWhiteSpace(xml[lineStart..start]) && string.IsNullOrWhiteSpace(after)
            ? (lineStart, lineEnd < 0 ? xml.Length : lineEnd + 1)
            : (start, end);
    }

    /// <summary>The whitespace before <paramref name="start"/> on its line, or "" when other text precedes it.</summary>
    public static string IndentOf(string xml, int start)
    {
        var lineStart = start == 0 ? 0 : xml.LastIndexOf('\n', start - 1) + 1;
        var before = xml[lineStart..start];
        return string.IsNullOrWhiteSpace(before) ? before : "";
    }

    public static string NewLineOf(string xml) => xml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Attribute escaping; line breaks and tabs become character references so they survive a reload.</summary>
    public static string Escape(string value, char quote)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '"' when quote == '"' => "&quot;",
                '\'' when quote == '\'' => "&apos;",
                '\n' => "&#xA;",
                '\r' => "&#xD;",
                '\t' => "&#x9;",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private static string Decode(string raw, char quote)
    {
        try { return XElement.Parse($"<a v={quote}{raw}{quote}/>").Attribute("v")!.Value; }
        catch (XmlException) { return raw; }
    }

    private static bool IsNameEnd(char c) => char.IsWhiteSpace(c) || c is '/' or '>';
}
