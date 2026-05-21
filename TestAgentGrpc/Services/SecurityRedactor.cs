using System.Text.RegularExpressions;

namespace TestAgentGrpc.Services;

/// <summary>
/// Centralized secret redaction for audit logs, application logs, and UI-safe text.
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

public sealed class CommandPolicySettings
{
    /// <summary>
    /// Disabled: no checks. AuditOnly: log violations. Enforce: reject unsafe commands.
    /// Default is AuditOnly to surface risk without breaking existing WatchList pipelines.
    /// </summary>
    public string Mode { get; set; } = "AuditOnly";

    /// <summary>Allowed command roots or exact command prefixes. Empty means no allowlist check.</summary>
    public List<string> AllowedCommandPrefixes { get; set; } = [];

    /// <summary>Reject or audit command lines containing obvious shell chaining/injection tokens.</summary>
    public bool DetectShellChaining { get; set; } = true;

    /// <summary>
    /// When non-empty, only executables whose resolved full path starts with one of these
    /// directory prefixes are allowed. Paths are normalized and compared case-insensitively.
    /// Example: ["C:\\TestScripts", "C:\\Program Files\\MyApp"]
    /// </summary>
    public List<string> AllowedExecutablePaths { get; set; } = [];
}

public readonly record struct CommandPolicyResult(bool IsAllowed, string Reason)
{
    public static CommandPolicyResult Allowed() => new(true, string.Empty);
    public static CommandPolicyResult Denied(string reason) => new(false, reason);
}

public sealed class CommandPolicyEvaluator
{
    private readonly CommandPolicySettings _settings;

    public CommandPolicyEvaluator(CommandPolicySettings settings)
    {
        _settings = settings;
    }

    public bool IsDisabled => string.Equals(_settings.Mode, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsEnforced => string.Equals(_settings.Mode, "Enforce", StringComparison.OrdinalIgnoreCase);
    public bool IsAuditOnly => string.Equals(_settings.Mode, "AuditOnly", StringComparison.OrdinalIgnoreCase);

    public CommandPolicyResult Evaluate(string command, string arguments)
    {
        if (IsDisabled)
            return CommandPolicyResult.Allowed();

        var cmd = command.Trim().Trim('"');
        var full = $"{command} {arguments}";

        if (string.IsNullOrWhiteSpace(cmd))
            return CommandPolicyResult.Denied("Command is empty");

        if (SecurityRedactor.ContainsSensitiveKeyword(full))
            return CommandPolicyResult.Denied("Command line contains sensitive keyword");

        if (_settings.DetectShellChaining && ContainsShellChaining(arguments))
            return CommandPolicyResult.Denied("Arguments contain shell chaining or redirection tokens");

        if (_settings.AllowedCommandPrefixes.Count > 0 &&
            !_settings.AllowedCommandPrefixes.Any(prefix => cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandPolicyResult.Denied("Command is outside allowed command prefixes");
        }

        // Safe path allowlist: resolve the executable's full path and verify it
        // resides under one of the allowed directories.
        if (_settings.AllowedExecutablePaths.Count > 0)
        {
            var pathResult = ValidateExecutablePath(cmd);
            if (!pathResult.IsAllowed)
                return pathResult;
        }

        return CommandPolicyResult.Allowed();
    }

    private CommandPolicyResult ValidateExecutablePath(string command)
    {
        // Try to resolve the full path of the executable
        string? resolvedPath = null;

        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            resolvedPath = Path.GetFullPath(command);
        }
        else
        {
            // Search PATH environment variable for the executable
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extensions = new[] { "", ".exe", ".cmd", ".bat", ".ps1" };

            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate))
                    {
                        resolvedPath = Path.GetFullPath(candidate);
                        break;
                    }
                }
                if (resolvedPath != null) break;
            }
        }

        if (resolvedPath == null)
            return CommandPolicyResult.Denied($"Executable not found on safe path: {command}");

        // Normalize and compare against allowed paths (case-insensitive on Windows)
        var normalizedResolved = Path.GetFullPath(resolvedPath);
        foreach (var allowed in _settings.AllowedExecutablePaths)
        {
            var normalizedAllowed = Path.GetFullPath(allowed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                + Path.DirectorySeparatorChar;

            if (normalizedResolved.StartsWith(normalizedAllowed, StringComparison.OrdinalIgnoreCase))
                return CommandPolicyResult.Allowed();
        }

        return CommandPolicyResult.Denied(
            $"Executable path '{resolvedPath}' is not under any allowed directory");
    }

    private static bool ContainsShellChaining(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        return arguments.Contains("&&", StringComparison.Ordinal) ||
               arguments.Contains("||", StringComparison.Ordinal) ||
               arguments.Contains(";", StringComparison.Ordinal) ||
               arguments.Contains(">", StringComparison.Ordinal) ||
               arguments.Contains("<", StringComparison.Ordinal) ||
               arguments.Contains("`", StringComparison.Ordinal);
    }
}
