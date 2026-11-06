using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

public sealed record InlineCredentialMatch(string Name, string Value, int ValueIndex, int ValueLength);

/// <summary>Finds literal credential assignments in action text without putting values in diagnostics.</summary>
public static class InlineCredentialFinder
{
    private static readonly Regex Assignment = new(
        @"(?<name>password|passwd|pwd|token|secret|api[_-]?key)(?:\s*[:=]\s*|\s+)[\""']?(?<value>[^ \t\r\n;,\""']+)[\""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<InlineCredentialMatch> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        return Assignment.Matches(text)
            .Select(match => new InlineCredentialMatch(
                match.Groups["name"].Value,
                match.Groups["value"].Value,
                match.Groups["value"].Index,
                match.Groups["value"].Length))
            .Where(match => !IsReference(match.Value))
            .ToArray();
    }

    public static bool ContainsLiteral(string? text) => Find(text).Count > 0;

    public static bool CanMoveToSecretEnvironment(ActionConfig action) =>
        (Find(action.Command).Count > 0 || Find(action.Parameters).Count > 0)
        && Find(action.Command).Concat(Find(action.Parameters))
            .All(match => !match.Value.Contains(';') && !match.Value.Contains('\r') && !match.Value.Contains('\n'))
        && (Path.GetExtension(action.Command.Trim().Trim('"')).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(action.Command.Trim().Trim('"')).Equals(".bat", StringComparison.OrdinalIgnoreCase));

    public static bool MoveToSecretEnvironment(ActionConfig action)
    {
        var matches = Find(action.Command).Select(m => (Field: nameof(action.Command), Match: m))
            .Concat(Find(action.Parameters).Select(m => (Field: nameof(action.Parameters), Match: m)))
            .ToList();
        if (matches.Count == 0) return false;

        if (!CanMoveToSecretEnvironment(action)) return false;

        var existingNames = SecretEnvironment.Parse(action.SecretEnv)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suffix = 1;
        var replacements = new Dictionary<(string Field, int Index), string>();

        foreach (var (field, match) in matches)
        {
            var baseName = "SECRET_" + Regex.Replace(match.Name, @"[^A-Za-z0-9_]", "_").ToUpperInvariant();
            var variable = baseName;
            while (!existingNames.Add(variable))
                variable = $"{baseName}_{suffix++}";

            action.SecretEnv = string.IsNullOrWhiteSpace(action.SecretEnv)
                ? $"{variable}={match.Value}"
                : $"{action.SecretEnv.TrimEnd(';')};{variable}={match.Value}";

            var reference = $"%{variable}%";
            replacements[(field, match.ValueIndex)] = reference;
        }

        action.Command = ReplaceValues(action.Command, nameof(action.Command), matches, replacements);
        action.Parameters = ReplaceValues(action.Parameters, nameof(action.Parameters), matches, replacements);
        return true;
    }

    private static string ReplaceValues(
        string text,
        string field,
        IReadOnlyList<(string Field, InlineCredentialMatch Match)> matches,
        IReadOnlyDictionary<(string Field, int Index), string> replacements)
    {
        foreach (var match in matches.Where(m => m.Field == field).OrderByDescending(m => m.Match.ValueIndex))
        {
            var start = match.Match.ValueIndex;
            text = text[..start] + replacements[(field, start)] + text[(start + match.Match.ValueLength)..];
        }
        return text;
    }

    private static bool IsReference(string value) =>
        value.StartsWith("[_", StringComparison.Ordinal)
        || value.StartsWith('%') && value.EndsWith('%')
        || value.StartsWith("$env:", StringComparison.OrdinalIgnoreCase);
}
