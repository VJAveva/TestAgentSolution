using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Features;

/// <summary>
/// One Code Churn repository from Features.json "CodeChurn:Repositories". <see cref="CredentialRef"/> names the
/// environment variable that holds the secret; the value itself is never stored, logged or returned.
/// </summary>
public sealed record CodeChurnRepository(
    string Name,
    string? Url,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> IncludePaths,
    IReadOnlyList<string> ExcludePaths,
    string? CredentialRef);

public static class CodeChurnRepositoryRules
{
    private static readonly Regex EnvVarName = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    // Azure DevOps PATs are 52 (classic) or 84 (current) characters of [a-zA-Z0-9].
    private static readonly Regex PatShape = new(@"^[A-Za-z0-9]{52}$|^[A-Za-z0-9]{84}$", RegexOptions.CultureInvariant);

    /// <summary>Why the list cannot be saved; null when it is valid.</summary>
    public static string? Validate(IReadOnlyList<CodeChurnRepository> repositories)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Name))
                return "Every repository needs a Name.";
            if (!names.Add(repo.Name.Trim()))
                return $"Repository '{repo.Name}' is listed twice.";
            if (!string.IsNullOrWhiteSpace(repo.Url)
                && !(Uri.TryCreate(repo.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                return $"Repository '{repo.Name}': Url must be an https address.";
            if (repo.Branches.Any(string.IsNullOrWhiteSpace))
                return $"Repository '{repo.Name}' has an empty branch name.";
            if (CredentialRefProblem(repo.CredentialRef) is { } problem)
                return $"Repository '{repo.Name}': {problem}";
        }
        return null;
    }

    /// <summary>CredentialRef must be the NAME of an environment variable, never the secret itself.</summary>
    public static string? CredentialRefProblem(string? credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef)) return null;
        if (PatShape.IsMatch(credentialRef))
            return "CredentialRef looks like a token. Store the token in an environment variable and enter its name.";
        return EnvVarName.IsMatch(credentialRef)
            ? null
            : "CredentialRef must be an environment variable name (letters, digits, underscore).";
    }

    /// <summary>
    /// True when a changed file is inside the repository's scope. Entries without wildcards are folder prefixes
    /// ("src/"); entries with * or ? are globs. Exclude wins over include; no includes means everything.
    /// </summary>
    public static bool InScope(CodeChurnRepository repo, string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        if (repo.ExcludePaths.Any(x => Matches(p, x))) return false;
        return repo.IncludePaths.Count == 0 || repo.IncludePaths.Any(x => Matches(p, x));
    }

    private static bool Matches(string path, string entry)
    {
        var e = entry.Replace('\\', '/').TrimStart('/');
        if (e.Length == 0) return false;
        if (e.IndexOfAny(['*', '?']) < 0)
            return path.StartsWith(e, StringComparison.OrdinalIgnoreCase);
        var pattern = "^" + Regex.Escape(e).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Reads "CodeChurn": { "Repositories": [...] }. Null when the section is absent (legacy behaviour).</summary>
    internal static IReadOnlyList<CodeChurnRepository>? Parse(JsonElement root, Action<string> warn)
    {
        if (!TryGet(root, "CodeChurn", out var churn) || churn.ValueKind != JsonValueKind.Object) return null;
        if (!TryGet(churn, "Repositories", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array)
        {
            warn("CodeChurn:Repositories must be a list; treated as empty.");
            return [];
        }

        var result = new List<CodeChurnRepository>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { warn("A CodeChurn repository entry is not an object; ignored."); continue; }
            var repo = new CodeChurnRepository(
                Str(item, "Name") ?? "",
                Str(item, "Url"),
                Strings(item, "Branches"),
                Strings(item, "IncludePaths"),
                Strings(item, "ExcludePaths"),
                Str(item, "CredentialRef"));

            if (Validate([.. result, repo]) is { } problem)
            {
                warn($"{problem} Entry ignored.");
                continue;
            }
            result.Add(repo with { Name = repo.Name.Trim() });
        }
        return result;
    }

    internal static JsonObject ToJson(CodeChurnRepository r)
    {
        var o = new JsonObject { ["Name"] = r.Name.Trim() };
        if (!string.IsNullOrWhiteSpace(r.Url)) o["Url"] = r.Url.Trim();
        o["Branches"] = new JsonArray(r.Branches.Select(b => (JsonNode?)JsonValue.Create(b.Trim())).ToArray());
        o["IncludePaths"] = new JsonArray(r.IncludePaths.Select(b => (JsonNode?)JsonValue.Create(b.Trim())).ToArray());
        o["ExcludePaths"] = new JsonArray(r.ExcludePaths.Select(b => (JsonNode?)JsonValue.Create(b.Trim())).ToArray());
        if (!string.IsNullOrWhiteSpace(r.CredentialRef)) o["CredentialRef"] = r.CredentialRef.Trim();
        return o;
    }

    /// <summary>Stable text for change detection and cache keys.</summary>
    public static string Fingerprint(IReadOnlyList<CodeChurnRepository>? repos) =>
        repos is null ? "legacy" : string.Join("|", repos.Select(r =>
            $"{r.Name};{r.Url};{string.Join(",", r.Branches)};{string.Join(",", r.IncludePaths)};{string.Join(",", r.ExcludePaths)};{r.CredentialRef}"));

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : [];
}
