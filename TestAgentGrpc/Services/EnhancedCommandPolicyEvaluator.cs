using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TestAgentGrpc.Services;

/// <summary>
/// Enhanced command policy evaluator that loads allowlist/blocklist from an external JSON file.
/// Supports regex-based patterns, path-root validation, and automatic hot-reload.
/// Wraps the existing <see cref="CommandPolicyEvaluator"/> for backwards compatibility.
/// </summary>
public sealed class EnhancedCommandPolicyEvaluator : IDisposable
{
    private readonly CommandPolicyEvaluator _baseEvaluator;
    private readonly ILogger<EnhancedCommandPolicyEvaluator> _logger;
    private readonly string? _policyFilePath;
    private readonly FileSystemWatcher? _fileWatcher;
    private volatile CommandPolicyFile _policyFile = new();
    private readonly object _reloadLock = new();

    public EnhancedCommandPolicyEvaluator(
        CommandPolicySettings settings,
        ILogger<EnhancedCommandPolicyEvaluator> logger)
    {
        _baseEvaluator = new CommandPolicyEvaluator(settings);
        _logger = logger;
        _policyFilePath = settings.AllowlistPath;

        if (!string.IsNullOrEmpty(_policyFilePath) && File.Exists(_policyFilePath))
        {
            LoadPolicyFile();

            // Set up file watcher for hot-reload
            var dir = Path.GetDirectoryName(Path.GetFullPath(_policyFilePath))!;
            var fileName = Path.GetFileName(_policyFilePath);
            _fileWatcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _fileWatcher.Changed += (_, _) => LoadPolicyFile();
            _fileWatcher.EnableRaisingEvents = true;
        }
    }

    public bool IsDisabled => _baseEvaluator.IsDisabled;
    public bool IsEnforced => _baseEvaluator.IsEnforced;
    public bool IsAuditOnly => _baseEvaluator.IsAuditOnly;

    /// <summary>
    /// Evaluates a command against the full policy chain:
    /// 1. Blocklist (always applies in Enforce mode)
    /// 2. Allowlist (required unless caller is admin)
    /// 3. Path-root validation
    /// 4. Base evaluator checks (shell chaining, executable path)
    /// </summary>
    public CommandPolicyResult Evaluate(string command, string arguments, bool callerIsAdmin = false)
    {
        if (_baseEvaluator.IsDisabled)
            return CommandPolicyResult.Allowed();

        var cmd = command.Trim().Trim('"');
        var fullCommand = $"{command} {arguments}".Trim();

        // Step 1: Blocklist — always applies, even for admins
        var blockResult = CheckBlocklist(fullCommand);
        if (!blockResult.IsAllowed)
            return blockResult;

        // Step 2: Allowlist — admins can bypass
        if (!callerIsAdmin)
        {
            var allowResult = CheckAllowlist(cmd, arguments);
            if (!allowResult.IsAllowed)
                return allowResult;
        }

        // Step 3: Path-root validation
        var pathResult = CheckPathRoots(arguments);
        if (!pathResult.IsAllowed)
            return pathResult;

        // Step 4: Base evaluator (shell chaining, executable path resolution)
        return _baseEvaluator.Evaluate(command, arguments);
    }

    /// <summary>Reloads the policy file from disk. Called by hot-reload or RPC.</summary>
    public void Reload()
    {
        LoadPolicyFile();
        _logger.LogInformation("Command policy reloaded from {Path}", _policyFilePath);
    }

    private CommandPolicyResult CheckBlocklist(string fullCommand)
    {
        var policy = _policyFile;
        if (policy.Blocklist is null or { Count: 0 })
            return CommandPolicyResult.Allowed();

        foreach (var entry in policy.Blocklist)
        {
            if (string.IsNullOrEmpty(entry.Pattern)) continue;
            try
            {
                if (Regex.IsMatch(fullCommand, entry.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    return CommandPolicyResult.Denied($"Command matches blocklist pattern: {entry.Pattern}");
            }
            catch (RegexMatchTimeoutException)
            {
                return CommandPolicyResult.Denied($"Blocklist pattern evaluation timed out: {entry.Pattern}");
            }
        }

        return CommandPolicyResult.Allowed();
    }

    private CommandPolicyResult CheckAllowlist(string command, string arguments)
    {
        var policy = _policyFile;
        if (policy.Allowlist is null or { Count: 0 })
            return CommandPolicyResult.Allowed(); // No allowlist = allow all (backwards compat)

        if (policy.BlockOnUnknown != true)
            return CommandPolicyResult.Allowed(); // Not blocking unknown commands

        // Normalize so 'cmd' matches 'cmd.exe' — the controller may launch either the bare
        // name or the .exe; the allowlist should match regardless of extension.
        static string NormalizeExe(string name) =>
            Path.GetFileName(name).ToLowerInvariant() is var n && n.EndsWith(".exe", StringComparison.Ordinal)
                ? n[..^4] : n;

        var cmdName = NormalizeExe(command);

        foreach (var entry in policy.Allowlist)
        {
            var entryCmd = NormalizeExe(entry.Command ?? "");
            if (!string.Equals(cmdName, entryCmd, StringComparison.OrdinalIgnoreCase))
                continue;

            // Command matches — check argument pattern if specified
            if (string.IsNullOrEmpty(entry.ArgPattern))
                return CommandPolicyResult.Allowed();

            try
            {
                if (Regex.IsMatch(arguments, entry.ArgPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    return CommandPolicyResult.Allowed();
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Allowlist argument pattern timed out: {Pattern}", entry.ArgPattern);
            }
        }

        return CommandPolicyResult.Denied($"Command '{command}' is not in the allowlist");
    }

    private CommandPolicyResult CheckPathRoots(string arguments)
    {
        var policy = _policyFile;
        if (policy.AllowedPathRoots is null or { Count: 0 })
            return CommandPolicyResult.Allowed();

        // Extract potential file paths from arguments (simple heuristic: look for drive-letter paths)
        var pathPattern = @"[A-Za-z]:\\[^\s""']+";
        var matches = Regex.Matches(arguments, pathPattern);

        foreach (Match match in matches)
        {
            var path = match.Value.TrimEnd('"', '\'');
            var normalizedPath = Path.GetFullPath(path);
            var isAllowed = false;

            foreach (var root in policy.AllowedPathRoots)
            {
                var normalizedRoot = Path.GetFullPath(root.TrimEnd('\\')) + "\\";
                if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed)
                return CommandPolicyResult.Denied($"Path '{path}' is not under any allowed root directory");
        }

        return CommandPolicyResult.Allowed();
    }

    private void LoadPolicyFile()
    {
        if (string.IsNullOrEmpty(_policyFilePath) || !File.Exists(_policyFilePath))
            return;

        lock (_reloadLock)
        {
            try
            {
                var json = File.ReadAllText(_policyFilePath);
                var policy = JsonSerializer.Deserialize<CommandPolicyFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                _policyFile = policy ?? new();
                _logger.LogInformation(
                    "Loaded command policy: {AllowCount} allowlist, {BlockCount} blocklist, {PathCount} path roots",
                    _policyFile.Allowlist?.Count ?? 0,
                    _policyFile.Blocklist?.Count ?? 0,
                    _policyFile.AllowedPathRoots?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load command policy from {Path}", _policyFilePath);
            }
        }
    }

    public void Dispose()
    {
        _fileWatcher?.Dispose();
    }
}

/// <summary>
/// Represents the external command policy file (commandpolicy.json).
/// </summary>
public sealed class CommandPolicyFile
{
    [JsonPropertyName("Allowlist")]
    public List<AllowlistEntry>? Allowlist { get; set; }

    [JsonPropertyName("Blocklist")]
    public List<BlocklistEntry>? Blocklist { get; set; }

    [JsonPropertyName("AllowedPathRoots")]
    public List<string>? AllowedPathRoots { get; set; }

    [JsonPropertyName("BlockOnUnknown")]
    public bool? BlockOnUnknown { get; set; }
}

public sealed class AllowlistEntry
{
    [JsonPropertyName("Command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("ArgPattern")]
    public string? ArgPattern { get; set; }
}

public sealed class BlocklistEntry
{
    [JsonPropertyName("Pattern")]
    public string Pattern { get; set; } = "";
}
