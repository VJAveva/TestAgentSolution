namespace TestControllerGrpc.Services;

/// <summary>
/// Turns an action's <c>SecretEnv</c> into child-process environment variables.
/// </summary>
/// <remarks>
/// A credential passed as a command-line argument is visible in the process table to anyone who can
/// enumerate processes, and no amount of log redaction hides that. The environment block of a child
/// process is readable only by the owner and by an administrator, so this is where secrets belong.
/// </remarks>
public static class SecretEnvironment
{
    public const char PairSeparator = ';';

    /// <summary>
    /// Parses <c>NAME=value;NAME2=value2</c>. Malformed entries are skipped rather than throwing: a
    /// typo must not take down a pipeline, and the missing variable surfaces as the script's own
    /// "credentials are not set" error.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return [];

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var entry in spec.Split(PairSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;

            // Split on the FIRST '=' only - a password may legitimately contain one.
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var name = trimmed[..eq].Trim();
            if (name.Length == 0) continue;

            pairs.Add(new KeyValuePair<string, string>(name, trimmed[(eq + 1)..]));
        }

        return pairs;
    }

    /// <summary>
    /// Applies the pairs to a child process's environment and registers each value for redaction, so a
    /// script that echoes its own environment still cannot print the secret into our logs.
    /// </summary>
    public static int Apply(System.Diagnostics.ProcessStartInfo psi, string? spec)
    {
        var pairs = Parse(spec);
        foreach (var pair in pairs)
        {
            psi.Environment[pair.Key] = pair.Value;
            SecurityRedactor.RegisterSecretValue(pair.Value);
        }
        return pairs.Count;
    }

    /// <summary>Variable names only - safe to log, unlike the values.</summary>
    public static string DescribeNames(string? spec)
        => string.Join(", ", Parse(spec).Select(p => p.Key));
}
