using System.Text.RegularExpressions;

namespace TestControllerGrpc.Services;

/// <summary>
/// Centralized redaction for controller/WebApi logs, SignalR payloads, and execution status text.
/// </summary>
public static partial class SecurityRedactor
{
    public const string Redacted = "***REDACTED***";

    private static readonly string[] SensitiveWords =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
        "accesskey", "access_key", "clientsecret", "client_secret", "credential"
    ];

    /// <summary>Below this length a value is too generic to scrub without mangling ordinary text.</summary>
    private const int MinSecretLength = 4;

    private const int MaxTrackedSecrets = 512;

    // Copy-on-write: logging reads this on every line, so readers must never take a lock.
    // Ordered longest-first so an overlapping value cannot leave a fragment behind.
    private static volatile string[] _secretValues = [];
    private static readonly object SecretGate = new();

    /// <summary>
    /// Registers a resolved secret VALUE. The regexes above can only redact a secret that travels next
    /// to its name (-Password x, password=x). A credential passed as a POSITIONAL argument has no name
    /// to anchor on, so the value itself is the only thing we can match.
    /// </summary>
    public static void RegisterSecretValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinSecretLength) return;

        // An unresolved placeholder is not a secret, and scrubbing "[_RcloudPassword]" would hide
        // exactly the diagnostic that tells you a token failed to resolve.
        if (value.Contains('[') || value.Contains(']')) return;
        if (value == Redacted) return;

        lock (SecretGate)
        {
            if (_secretValues.Contains(value, StringComparer.Ordinal)) return;
            if (_secretValues.Length >= MaxTrackedSecrets) return;

            _secretValues = [.. _secretValues.Append(value).OrderByDescending(v => v.Length)];
        }
    }

    /// <summary>Test seam; production never needs to forget a secret.</summary>
    public static void ClearSecretValues()
    {
        lock (SecretGate)
            _secretValues = [];
    }

    public static int TrackedSecretCount => _secretValues.Length;

    private static string ScrubSecretValues(string text)
    {
        var secrets = _secretValues;
        if (secrets.Length == 0 || string.IsNullOrEmpty(text)) return text;

        foreach (var secret in secrets)
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);

        return text;
    }

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = value;
        result = JsonSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}{m.Groups[3].Value}");
        result = KeyValueSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");
        result = PowerShellSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");
        result = ConnectionStringPasswordRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");

        // Last: the named passes have already collapsed what they can, so this only has to catch
        // values that arrived without a name beside them.
        result = ScrubSecretValues(result);
        return result;
    }

    public static string RedactCommandLine(string command, string arguments)
    {
        var combined = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";
        return Redact(combined) ?? string.Empty;
    }

    public static bool ContainsSensitiveKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return SensitiveWords.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("(?i)(\\\"(?:password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key|clientsecret|client_secret|credential)\\\"\\s*:\\s*\\\")([^\\\"]*)(\\\")")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"(?i)(\b(?:password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key|clientsecret|client_secret|credential)\b\s*[:=]\s*)([^\s;,'""\)]+)")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)(-(?:password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key|clientsecret|client_secret|credential)\s+)([^\s]+)")]
    private static partial Regex PowerShellSecretRegex();

    [GeneratedRegex(@"(?i)(\b(?:Password|Pwd)\s*=\s*)([^;]+)")]
    private static partial Regex ConnectionStringPasswordRegex();
}
