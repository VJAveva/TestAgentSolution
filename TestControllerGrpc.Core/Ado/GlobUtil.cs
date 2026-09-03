using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Ado;

/// <summary>Minimal case-insensitive glob matcher (supports <c>*</c> and <c>?</c>) with a cached regex per pattern.</summary>
internal static class GlobUtil
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string? value, string? glob)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(glob))
            return false;
        return Cache.GetOrAdd(glob, ToRegex).IsMatch(value);
    }

    private static Regex ToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Decides whether a changed file is "noise" to hide from the modified-files list:
/// the repo's own &lt;RepoName&gt;.yaml/.yml pipeline file, plus any configured <c>Ado:IgnoredFilePatterns</c> globs.
/// </summary>
public static class FileNoiseFilter
{
    public static bool IsIgnored(string? path, string? repoName, IReadOnlyList<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        if (!string.IsNullOrWhiteSpace(repoName) &&
            (name.Equals($"{repoName}.yaml", StringComparison.OrdinalIgnoreCase) ||
             name.Equals($"{repoName}.yml", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (patterns is not null)
            foreach (var pattern in patterns)
                if (GlobUtil.IsMatch(path, pattern) || GlobUtil.IsMatch(name, pattern))
                    return true;
        return false;
    }

    /// <summary>True only for source files worth listing (.h/.cpp/.cs); excludes folders and non-source files.</summary>
    public static bool IsSourceFile(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
}
