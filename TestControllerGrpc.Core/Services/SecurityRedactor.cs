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

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = value;
        result = JsonSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}{m.Groups[3].Value}");
        result = KeyValueSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");
        result = PowerShellSecretRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");
        result = ConnectionStringPasswordRegex().Replace(result, m => $"{m.Groups[1].Value}{Redacted}");
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
