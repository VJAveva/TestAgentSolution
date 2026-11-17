using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.PipelineEditor;

public enum DiffLineKind { Unchanged, Removed, Added, Changed, Empty }

/// <summary>One row of a side-by-side comparison; either side is empty when the line exists only on the other.</summary>
public sealed record DiffRow(string Before, DiffLineKind BeforeKind, string After, DiffLineKind AfterKind)
{
    public bool IsChange => BeforeKind != DiffLineKind.Unchanged || AfterKind != DiffLineKind.Unchanged;
}

/// <summary>What changed in one pipeline or template: its title and the side-by-side rows (secrets masked).</summary>
public sealed record PipelineChange(string Title, IReadOnlyList<DiffRow> Rows)
{
    public int ChangedLines => Rows.Count(r => r.IsChange);
}

/// <summary>"What changed" (section 8): before / after, side by side, with secrets never shown.</summary>
public static class PipelineDiff
{
    private static readonly SideBySideDiffBuilder Builder = new(new Differ());
    private static readonly Regex SecretAttribute = new(
        @"\b(?<name>Password|SecretEnv)\s*=\s*(?<q>[""'])(?<value>.*?)\k<q>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static PipelineChange Compare(string title, string before, string after)
    {
        var model = Builder.BuildDiffModel(Normalise(before), Normalise(after), ignoreWhitespace: false);
        var rows = model.OldText.Lines.Zip(model.NewText.Lines, (left, right) =>
            new DiffRow(Mask(left.Text), Kind(left.Type), Mask(right.Text), Kind(right.Type))).ToList();
        return new PipelineChange(title, rows);
    }

    /// <summary>Password values are hidden and SecretEnv shows variable names only; everything else goes through the redactor.</summary>
    public static string Mask(string? line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        // Redact only the text between the secret attributes: the redactor's Password rule would swallow the rest of the line.
        var builder = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in SecretAttribute.Matches(line))
        {
            builder.Append(SecurityRedactor.Redact(line[last..m.Index]));
            var name = m.Groups["name"].Value;
            var quote = m.Groups["q"].Value;
            var value = name.Equals("SecretEnv", StringComparison.OrdinalIgnoreCase)
                ? string.Join(";", m.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Split('=', 2)[0] + "=" + TokenDisplay.Mask))
                : TokenDisplay.Mask;
            builder.Append(name).Append('=').Append(quote).Append(value).Append(quote);
            last = m.Index + m.Length;
        }
        builder.Append(SecurityRedactor.Redact(line[last..]));
        return builder.ToString();
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static DiffLineKind Kind(ChangeType type) => type switch
    {
        ChangeType.Deleted => DiffLineKind.Removed,
        ChangeType.Inserted => DiffLineKind.Added,
        ChangeType.Modified => DiffLineKind.Changed,
        ChangeType.Imaginary => DiffLineKind.Empty,
        _ => DiffLineKind.Unchanged,
    };
}

/// <summary>One token of a pipeline: its value now (secrets masked), where it comes from and which steps use it.</summary>
public sealed record PipelineParameterRow(string Token, string Value, string Source, string UsedBy, bool IsUnused, bool IsUnresolved)
{
    public string Note => IsUnresolved ? "Not defined" : IsUnused ? "Unused" : "";
}

/// <summary>The Parameters view (section 6): every token the pipeline defines or uses. Read-only.</summary>
public static class PipelineParameterTable
{
    private static readonly Regex TokenPattern = new(@"\[(?<name>_\w+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PipelineParameterRow> Build(WatchItemConfig item, WatchListConfig? context)
    {
        var execution = new PipelineExecutionContext { WatchItemTag = item.Tag, GlobalVariablesFile = context?.GlobalVariablesFile ?? "" };
        ParameterResolver.LoadForWatchItem(execution, item);

        var users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var templates = (context?.Templates ?? []).Where(t => !string.IsNullOrEmpty(t.ID))
            .GroupBy(t => t.ID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var evt in item.Events)
            Collect(evt.Children, [], users, templates);

        var triggerFields = new[] { item.BuildNumberField, item.DropLocationField }
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.StartsWith('_') ? f : "_" + f)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = execution.Parameters.Keys.Where(k => k.StartsWith('_'))
            .Concat(users.Keys).Concat(triggerFields).Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Select(name =>
        {
            var used = users.TryGetValue(name, out var steps) ? string.Join(", ", steps.Distinct()) : "";
            if (triggerFields.Contains(name))
                return new PipelineParameterRow($"[{name}]", "(set by the trigger file)", nameof(TokenLayer.Trigger), used, used.Length == 0, false);
            if (!execution.Parameters.TryGetValue(name, out var value))
                return new PipelineParameterRow($"[{name}]", "", "Not defined", used, false, true);
            var layer = execution.ParameterRanks.TryGetValue(name, out var rank)
                ? TokenDisplay.LayerFromRank((ParameterRank)rank).ToString() : nameof(TokenLayer.Unknown);
            var shown = TokenDisplay.IsSecret(name) ? TokenDisplay.Mask : SecurityRedactor.Redact(value) ?? "";
            return new PipelineParameterRow($"[{name}]", shown, layer, used, used.Length == 0, false);
        })
        .OrderBy(r => r.IsUnresolved ? 0 : r.IsUnused ? 2 : 1)
        .ThenBy(r => r.Token, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static void Collect(IEnumerable<IActionNode> nodes, HashSet<string> visited,
        Dictionary<string, List<string>> users, Dictionary<string, TemplateConfig> templates)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig a:
                    var text = string.Join(" ", a.AgentName, a.Command, a.Parameters, a.CompletionCheckCommand,
                        a.To, a.From, a.Title, a.Body, a.Attachment, a.SecretEnv, a.UserName);
                    var step = string.IsNullOrEmpty(a.Tag) ? a.Command : a.Tag;
                    foreach (Match m in TokenPattern.Matches(text))
                        (users.TryGetValue(m.Groups["name"].Value, out var list) ? list : users[m.Groups["name"].Value] = []).Add(step);
                    break;
                case ActionGroupConfig g:
                    Collect(g.Children, visited, users, templates);
                    break;
                case RefConfig r when templates.TryGetValue(r.TemplateID, out var template) && visited.Add(r.TemplateID):
                    Collect(template.Children, visited, users, templates);
                    break;
            }
        }
    }
}
