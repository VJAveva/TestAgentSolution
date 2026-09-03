using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Text;

namespace TestControllerGrpc.Core.Impact.Query;

/// <summary>Turns a code change into a structured <see cref="ChangeDocument"/> ready for retrieval (P10).</summary>
public interface IChangeDocumentBuilder
{
    /// <summary>Builds the change document. Pure and deterministic — no I/O, never throws on malformed diffs.</summary>
    ChangeDocument Build(ImpactedArea area, ChangePayload payload);
}

/// <summary>
/// Extracts the highest-value retrieval signals from a change (P10). In descending value: human-readable
/// string literals (QA writes test titles in the product's own words), changed public API signatures,
/// changed C# symbols (via Roslyn in script mode, with a regex fallback), the PR narrative, and path
/// tokens. <see cref="ChangeDocument.RawText"/> repeats the strongest signals so BM25 favours them without
/// a field-weighted scorer. The class performs no I/O and never throws on malformed input.
/// </summary>
public sealed partial class ChangeDocumentBuilder : IChangeDocumentBuilder
{
    private const int RawTextCap = 12000;

    private readonly ImpactMappingOptions.KeywordOptions _keywords;

    /// <summary>Creates the builder from impact-mapping options (used only for tokenizer stop words).</summary>
    public ChangeDocumentBuilder(IOptions<ImpactMappingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keywords = options.Value.Keywords;
    }

    /// <inheritdoc />
    public ChangeDocument Build(ImpactedArea area, ChangePayload payload)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(payload);

        var literals = new OrderedStringSet();
        var symbols = new OrderedStringSet();
        var apiChanges = new OrderedStringSet();

        foreach (FileDiff file in payload.Diffs ?? [])
        {
            string content = ChangedContent(file);
            if (content.Length == 0)
            {
                continue;
            }

            if (IsCSharp(file.Path))
            {
                ExtractCSharp(content, literals, symbols, apiChanges);
            }
            else
            {
                ExtractPlainLiterals(content, literals);
            }
        }

        IReadOnlyList<string> pathTokens = ExtractPathTokens(payload.Diffs ?? []);
        string? narrative = BuildNarrative(payload);

        string rawText = ComposeRawText(literals.Items, apiChanges.Items, symbols.Items, narrative, pathTokens);
        string fingerprint = Fingerprint(rawText);

        return new ChangeDocument(
            area.AreaId, pathTokens, symbols.Items, literals.Items, apiChanges.Items, narrative, rawText, fingerprint);
    }

    // ── Diff line handling ────────────────────────────────────────────────────

    private static string ChangedContent(FileDiff file)
    {
        var builder = new StringBuilder();
        foreach (DiffHunk hunk in file.Hunks ?? [])
        {
            foreach (string line in ChangedLinesOf(hunk))
            {
                builder.Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> ChangedLinesOf(DiffHunk hunk)
    {
        string[] lines = hunk.Text.Replace("\r\n", "\n").Split('\n');
        bool hasMarkers = lines.Any(l =>
            l.Length > 0 && (l[0] == '+' || l[0] == '-') && !l.StartsWith("+++") && !l.StartsWith("---"));

        foreach (string line in lines)
        {
            if (line.StartsWith("@@"))
            {
                continue;
            }

            if (!hasMarkers)
            {
                yield return line; // hunk carries raw content, not a unified diff — treat all lines as changed
                continue;
            }

            if (line.StartsWith("+++") || line.StartsWith("---"))
            {
                continue;
            }

            if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
            {
                yield return line[1..];
            }
        }
    }

    // ── C# extraction (Roslyn) ────────────────────────────────────────────────

    private void ExtractCSharp(string content, OrderedStringSet literals, OrderedStringSet symbols, OrderedStringSet apiChanges)
    {
        try
        {
            CompilationUnitSyntax root = SyntaxFactory.ParseCompilationUnit(
                content, offset: 0, options: new CSharpParseOptions(kind: SourceCodeKind.Script));

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method:
                        symbols.Add(method.Identifier.ValueText);
                        if (IsPublicOrProtected(method.Modifiers))
                        {
                            apiChanges.Add(NormalizeMethod(method));
                        }

                        break;

                    case PropertyDeclarationSyntax property:
                        symbols.Add(property.Identifier.ValueText);
                        if (IsPublicOrProtected(property.Modifiers))
                        {
                            apiChanges.Add($"{property.Identifier.ValueText} -> {property.Type}");
                        }

                        break;

                    case TypeDeclarationSyntax type: // class / struct / interface / record
                        symbols.Add(type.Identifier.ValueText);
                        if (IsPublicOrProtected(type.Modifiers))
                        {
                            apiChanges.Add(type.Identifier.ValueText);
                        }

                        break;

                    case InvocationExpressionSyntax invocation:
                        string? callee = InvocationName(invocation);
                        if (callee is not null)
                        {
                            symbols.Add(callee);
                        }

                        break;
                }
            }

            foreach (SyntaxToken token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken))
                {
                    AddLiteralIfHuman(token.ValueText, literals);
                }
            }
        }
        catch
        {
            // Roslyn rarely throws (it produces error nodes), but the spec mandates a never-throw fallback.
            RegexSymbolFallback(content, symbols);
            ExtractPlainLiterals(content, literals);
        }
    }

    private static bool IsPublicOrProtected(SyntaxTokenList modifiers)
        => modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));

    private static string NormalizeMethod(MethodDeclarationSyntax method)
    {
        IEnumerable<string> parameterTypes = method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "var");
        return $"{method.Identifier.ValueText}({string.Join(", ", parameterTypes)}) -> {method.ReturnType}";
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private void RegexSymbolFallback(string content, OrderedStringSet symbols)
    {
        foreach (Match match in TypeDeclKeyword().Matches(content))
        {
            symbols.Add(match.Groups[1].Value);
        }

        foreach (Match match in MethodDeclKeyword().Matches(content))
        {
            symbols.Add(match.Groups[1].Value);
        }
    }

    // ── Literals ──────────────────────────────────────────────────────────────

    private static void ExtractPlainLiterals(string content, OrderedStringSet literals)
    {
        foreach (Match match in QuotedString().Matches(content))
        {
            AddLiteralIfHuman(match.Groups[1].Value, literals);
        }
    }

    private static void AddLiteralIfHuman(string value, OrderedStringSet literals)
    {
        if (IsHumanLiteral(value))
        {
            literals.Add(value.Trim());
        }
    }

    private static bool IsHumanLiteral(string value)
    {
        if (value.Length <= 4)
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            return false; // path
        }

        if (value.Contains('{') || value.Contains('}'))
        {
            return false; // format specifier / log template
        }

        if (GuidLike().IsMatch(value) || DottedIdentifier().IsMatch(value))
        {
            return false;
        }

        // Primary signal: multi-word human text. Otherwise accept only plain word-like literals.
        return value.Contains(' ') || value.All(char.IsLetter);
    }

    // ── Narrative ─────────────────────────────────────────────────────────────

    private static string? BuildNarrative(ChangePayload payload)
    {
        string combined = string.Join('\n',
            new[] { payload.PrTitle, payload.PrDescription }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (combined.Length == 0)
        {
            return null;
        }

        string stripped = MarkdownLink().Replace(combined, "$1");
        stripped = HtmlTag().Replace(stripped, " ");
        stripped = Url().Replace(stripped, " ");
        stripped = IssueLink().Replace(stripped, " ");
        stripped = MarkdownNoise().Replace(stripped, " ");
        stripped = Whitespace().Replace(stripped, " ").Trim();

        return stripped.Length == 0 ? null : stripped;
    }

    // ── Path tokens ───────────────────────────────────────────────────────────

    private IReadOnlyList<string> ExtractPathTokens(IReadOnlyList<FileDiff> diffs)
    {
        var tokens = new OrderedStringSet();
        foreach (FileDiff file in diffs)
        {
            string[] segments = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            // Filename plus the two closest parent folders.
            IEnumerable<string> nearest = segments.Reverse().Take(3);
            foreach (string token in Tokenizer.Tokenize(string.Join(' ', nearest), _keywords))
            {
                tokens.Add(token);
            }
        }

        return tokens.Items;
    }

    // ── Raw text + fingerprint ────────────────────────────────────────────────

    private static string ComposeRawText(
        IReadOnlyList<string> literals, IReadOnlyList<string> apiChanges,
        IReadOnlyList<string> symbols, string? narrative, IReadOnlyList<string> pathTokens)
    {
        var builder = new StringBuilder();
        AppendRepeated(builder, literals, 3);    // highest-value signal
        AppendRepeated(builder, apiChanges, 3);
        AppendRepeated(builder, symbols, 1);
        if (narrative is not null)
        {
            builder.Append(narrative).Append('\n');
        }

        AppendRepeated(builder, pathTokens, 1);

        string raw = builder.ToString();
        return raw.Length > RawTextCap ? raw[..RawTextCap] : raw;
    }

    private static void AppendRepeated(StringBuilder builder, IReadOnlyList<string> items, int times)
    {
        for (int i = 0; i < times; i++)
        {
            foreach (string item in items)
            {
                builder.Append(item).Append('\n');
            }
        }
    }

    private static string Fingerprint(string rawText)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawText))).ToLowerInvariant();

    private static bool IsCSharp(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

    // ── Regexes ───────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidLike();

    [GeneratedRegex(@"^[A-Za-z]+\.[A-Za-z.]+$")]
    private static partial Regex DottedIdentifier();

    [GeneratedRegex("\"([^\"]{5,})\"")]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"\b(?:class|interface|struct|record|enum)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypeDeclKeyword();

    [GeneratedRegex(@"\b(?:public|private|protected|internal|static|async|virtual|override|sealed)\s+[\w<>\[\],.\s]+?\s+([A-Za-z_]\w*)\s*\(")]
    private static partial Regex MethodDeclKeyword();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"\b[A-Za-z]{0,4}#\d+\b")]
    private static partial Regex IssueLink();

    [GeneratedRegex(@"[#*_`>]+")]
    private static partial Regex MarkdownNoise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>First-appearance-ordered distinct string collector (deterministic, Ordinal).</summary>
    private sealed class OrderedStringSet
    {
        private readonly List<string> _items = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Items => _items;

        public void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && _seen.Add(value))
            {
                _items.Add(value);
            }
        }
    }
}
