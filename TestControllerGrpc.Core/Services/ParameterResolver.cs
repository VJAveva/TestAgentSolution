using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Handles [Token] parameter resolution for the action pipeline.
///
/// Tokens like [BuildNumber], [EmailAddress], [DropLocation] are replaced
/// from two sources:
///   1. The trigger file content (CSV: _Key,Value or Key=Value per line)
///   2. Initialize → ParameterFile (CSV: _Key,Value or Key=Value per line)
///
/// Keys with a leading underscore (e.g. _BuildNumber) are stored both with
/// and without the prefix so [BuildNumber] and [_BuildNumber] both resolve.
/// </summary>
public static partial class ParameterResolver
{
    private static readonly Regex TokenPattern = TokenRegex();

    [GeneratedRegex(@"\[(\w+)\]")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Loads parameters from an Initialize ParameterFile.
    /// Supports two formats:
    ///   1. Comma-delimited: <c>_Key,Value</c> or <c>Key,,val1,val2,val3</c> (multi-value joined with commas)
    ///   2. Equals-delimited: <c>Key=Value</c>
    /// Lines starting with '#' are treated as comments.
    /// Keys with leading underscore are stored both with and without the underscore
    /// so tokens like [ControllerName] and [_ControllerName] both resolve.
    /// </summary>
    public static void LoadParameterFile(
        PipelineExecutionContext ctx,
        string parameterFilePath,
        ParameterRank rank = ParameterRank.ParameterFile)
    {
        if (!File.Exists(parameterFilePath)) return;

        try
        {
            foreach (var (key, value) in ParseParameterFile(parameterFilePath))
                SetParameter(ctx, key, value, rank);
        }
        catch (IOException)
        {
            // File may be locked by another process — acceptable to skip silently
        }
    }

    /// <summary>
    /// Parses a parameter file into key-value pairs.
    /// Supports comma-delimited (<c>_Key,Value</c>) and equals-delimited (<c>Key=Value</c>) formats.
    /// For lines with multiple commas like <c>EmailAddress,,a@b.com,c@d.com</c>,
    /// the value is all parts after the first comma, joined with commas (preserving empty segments).
    /// </summary>
    public static List<(string Key, string Value)> ParseParameterFile(string filePath)
    {
        var result = new List<(string Key, string Value)>();
        if (!File.Exists(filePath)) return result;

        foreach (var line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            // Try comma-delimited first (most parameter files use this format)
            var commaIdx = line.IndexOf(',');
            if (commaIdx > 0)
            {
                var key = line[..commaIdx].Trim();
                var value = line[(commaIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(key))
                    result.Add((key, value));
                continue;
            }

            // Fall back to equals-delimited
            var eqParts = line.Split('=', 2);
            if (eqParts.Length == 2)
            {
                var key = eqParts[0].Trim();
                var value = eqParts[1].Trim();
                if (!string.IsNullOrEmpty(key))
                    result.Add((key, value));
            }
        }

        return result;
    }

    /// <summary>
    /// Saves key-value pairs back to a parameter file in comma-delimited format.
    /// </summary>
    /// <summary>
    /// True when the path is a layered JSON config. Such a file must never be rewritten as flat
    /// key,value lines - doing so discards global/profiles/pipelines and every other pipeline's
    /// settings with it.
    /// </summary>
    public static bool IsLayeredConfig(string filePath) =>
        !string.IsNullOrEmpty(filePath)
        && filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the build number and drop location inside a layered JSON config, leaving every other
    /// key untouched. Targets pipelines[tag] when a tag is supplied, otherwise the global layer.
    /// Returns false when the file is missing or unreadable.
    /// </summary>
    public static bool TryUpdateJsonBuild(
        string filePath,
        string? pipelineTag,
        string buildNumber,
        string dropLocation)
    {
        var config = ReadJsonConfig(filePath);
        if (config is null) return false;

        var target = string.IsNullOrWhiteSpace(pipelineTag)
            ? config.Global
            : config.Pipelines.TryGetValue(pipelineTag, out var pinned)
                ? pinned
                : config.Pipelines[pipelineTag] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        target["_BuildNumber"] = buildNumber;
        target["_DropLocation"] = dropLocation;

        var json = JsonSerializer.Serialize(config, WriteJsonOptions);
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
        return true;
    }

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void SaveParameterFile(string filePath, IEnumerable<(string Key, string Value)> entries)
    {
        if (IsLayeredConfig(filePath))
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(filePath)}' is a layered JSON config. Writing flat key,value lines would destroy it.");
        }

        var lines = entries.Select(e => $"{e.Key},{e.Value}").ToList();
        var tempPath = filePath + ".tmp";
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>
    /// Applies a value unless a higher-ranked source already supplied that key. Equal rank still
    /// overwrites, so a later Initialize node keeps replacing an earlier one as it always has.
    /// </summary>
    public static void SetParameter(PipelineExecutionContext ctx, string key, string value, ParameterRank rank)
    {
        if (string.IsNullOrEmpty(key)) return;

        // Register before the rank check: a value that loses the race is still a live secret and can
        // still reach a log. TokenDisplay.IsSecret is the same rule the UI masks on.
        if (TokenDisplay.IsSecret(key))
            SecurityRedactor.RegisterSecretValue(value);

        Apply(ctx, key, value, rank);

        // Both [BuildNumber] and [_BuildNumber] must resolve, so the alias carries the same rank.
        if (key.StartsWith('_') && key.Length > 1)
            Apply(ctx, key[1..], value, rank);

        static void Apply(PipelineExecutionContext ctx, string key, string value, ParameterRank rank)
        {
            if (ctx.ParameterRanks.TryGetValue(key, out var winning) && winning > (int)rank)
                return;

            ctx.Parameters[key] = value;
            ctx.ParameterRanks[key] = (int)rank;
        }
    }

    /// <summary>Applies caller-supplied values for this run only. Outranks every file source.</summary>
    public static void ApplyRunOverrides(
        PipelineExecutionContext ctx,
        IEnumerable<KeyValuePair<string, string>> overrides)
    {
        foreach (var entry in overrides)
            SetParameter(ctx, entry.Key, entry.Value, ParameterRank.RunOverride);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads the layered JSON config, or null when it is missing or unreadable.</summary>
    public static PipelineParameterConfig? ReadJsonConfig(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<PipelineParameterConfig>(
                File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the layered JSON config: shared globals, then the named stage profile, then any
    /// build pinned to this pipeline. Each layer carries its own rank, so a pin is not undone by
    /// a profile and neither is undone by an Initialize node running later.
    /// </summary>
    public static void LoadJsonConfig(
        PipelineExecutionContext ctx,
        string filePath,
        string? profile,
        string? pipelineTag)
    {
        TryLoadJsonConfig(ctx, filePath, profile, pipelineTag);
    }

    /// <summary>
    /// As <see cref="LoadJsonConfig"/>, but reports whether the file could actually be read. A
    /// silent false here is what lets a run reach an agent with literal [Token] text.
    /// </summary>
    public static bool TryLoadJsonConfig(
        PipelineExecutionContext ctx,
        string filePath,
        string? profile,
        string? pipelineTag)
    {
        var config = ReadJsonConfig(filePath);
        if (config is null) return false;

        ApplyJsonLayers(ctx, config, profile, pipelineTag);
        return true;
    }

    private static void ApplyJsonLayers(
        PipelineExecutionContext ctx,
        PipelineParameterConfig config,
        string? profile,
        string? pipelineTag)
    {
        foreach (var entry in config.Global)
            SetParameter(ctx, entry.Key, entry.Value, ParameterRank.Global);

        if (!string.IsNullOrWhiteSpace(profile)
            && config.Profiles.TryGetValue(profile, out var stage))
        {
            foreach (var entry in stage)
                SetParameter(ctx, entry.Key, entry.Value, ParameterRank.ParameterFile);
        }

        if (!string.IsNullOrWhiteSpace(pipelineTag)
            && config.Pipelines.TryGetValue(pipelineTag, out var pinned))
        {
            foreach (var entry in pinned)
                SetParameter(ctx, entry.Key, entry.Value, ParameterRank.PipelinePin);
        }
    }

    /// <summary>
    /// Loads one Initialize node's parameter source, returning null on success or a caller-safe
    /// reason on failure. Unlike the older void loaders, a missing file or a profile that is not in
    /// the file is REPORTED rather than skipped: silently continuing is what let a run reach an
    /// agent with literal [Token] text, failing far from the real cause.
    /// </summary>
    public static string? TryLoadInitializeSource(
        PipelineExecutionContext ctx,
        string filePath,
        string? profile,
        string? pipelineTag)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "no ParameterFile is configured.";

        if (!File.Exists(filePath))
            return $"parameter file '{filePath}' does not exist.";

        if (!IsLayeredConfig(filePath))
        {
            LoadParameterFile(ctx, filePath);
            return null;
        }

        var config = ReadJsonConfig(filePath);
        if (config is null)
            return $"parameter file '{filePath}' could not be read as JSON.";

        if (!string.IsNullOrWhiteSpace(profile) && !config.Profiles.ContainsKey(profile))
        {
            var known = config.Profiles.Count == 0
                ? "the file defines no profiles at all"
                : "defined profiles are: " + string.Join(", ", config.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            return $"profile '{profile}' is not defined in '{Path.GetFileName(filePath)}' - {known}.";
        }

        ApplyJsonLayers(ctx, config, profile, pipelineTag);
        return null;
    }

    /// <summary>
    /// Fields of an <see cref="ActionConfig"/> that are NOT substituted before use, so a bracketed
    /// value in them is literal text rather than a token. Everything else is discovered by
    /// reflection: an explicit include-list drifts out of date the moment a field is added, which is
    /// how <c>To</c>, <c>Title</c> and <c>Body</c> went unchecked and mailed literal "[_EmailCheck]".
    /// </summary>
    private static readonly HashSet<string> NonSubstitutedFields = new(StringComparer.Ordinal)
    {
        nameof(ActionConfig.NodeType),
        nameof(ActionConfig.NodeId),
        nameof(ActionConfig.Tag),
        nameof(ActionConfig.ResolvedTag),
        nameof(ActionConfig.Order),
        nameof(ActionConfig.Comment),
        nameof(ActionConfig.SkipReason),
        nameof(ActionConfig.SkippedBy),
        nameof(ActionConfig.RetryBackoff),
        nameof(ActionConfig.RetryOnExitCodes),
    };

    /// <summary>Cached because this runs once per action on the dispatch path.</summary>
    private static readonly System.Reflection.PropertyInfo[] SubstitutedFields =
        typeof(ActionConfig)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string)
                        && p.CanRead
                        && !NonSubstitutedFields.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Fields where a bracketed word is prose unless it carries the <c>_</c> parameter prefix. An
    /// email subject or body legitimately reads "[INFO] build finished", and refusing to run over
    /// that would be worse than the bug this guards; "[_BuildNumber]" in the same text is still a
    /// parameter reference and still fails. Every other substituted field is strict - a stray
    /// bracket in a command line or a recipient address is never intentional.
    /// </summary>
    private static readonly HashSet<string> ProseTolerantFields = new(StringComparer.Ordinal)
    {
        nameof(ActionConfig.Title),
        nameof(ActionConfig.Body),
    };

    /// <summary>
    /// Token names referenced by an action that have no value in the context. Covers every field
    /// <see cref="ResolveAction"/> substitutes — the command line AND the email delivery fields —
    /// because an unresolved token anywhere means a parameter source failed to load. A non-empty
    /// result must stop the action: running on sends literal "[Token]" text to a shell or a mailbox.
    /// </summary>
    public static List<string> FindUnresolvedTokens(ActionConfig action, PipelineExecutionContext ctx)
    {
        var missing = new List<string>();

        foreach (var field in SubstitutedFields)
        {
            if (field.GetValue(action) is not string value || value.Length == 0) continue;

            var underscoreOnly = ProseTolerantFields.Contains(field.Name);

            foreach (Match match in TokenPattern.Matches(value))
            {
                var key = match.Groups[1].Value;
                if (underscoreOnly && !key.StartsWith('_')) continue;

                if (!ctx.Parameters.ContainsKey(key)
                    && !missing.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(key);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Resolves every Initialize source declared by a WatchItem, honouring each node's Profile.
    /// Always prefer this over calling <see cref="ParseParameterFile"/> directly: that is the CSV
    /// parser, and pointing it at a JSON config turns whole lines into keys.
    /// </summary>
    public static void LoadForWatchItem(PipelineExecutionContext ctx, WatchItemConfig watchItem)
    {
        if (string.IsNullOrEmpty(ctx.WatchItemTag))
            ctx.WatchItemTag = watchItem.Tag;

        foreach (var init in CollectInitializeNodes(watchItem))
        {
            if (init.ParameterFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                LoadJsonConfig(ctx, init.ParameterFile, init.Profile, watchItem.Tag);
            else
                LoadParameterFile(ctx, init.ParameterFile);
        }
    }

    /// <summary>Every Initialize node under a WatchItem, in declaration order, at any depth.</summary>
    public static List<InitializeConfig> CollectInitializeNodes(WatchItemConfig watchItem)
    {
        var nodes = new List<InitializeConfig>();
        foreach (var ev in watchItem.Events)
            Walk(ev.Children, nodes);
        return nodes;

        static void Walk(List<IActionNode> children, List<InitializeConfig> into)
        {
            foreach (var child in children)
            {
                if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
                    into.Add(init);
                else if (child is ActionGroupConfig group)
                    Walk(group.Children, into);
            }
        }
    }

    /// <summary>
    /// Replaces all [Token] placeholders in a string with resolved values.
    /// Unresolved tokens are left as-is.
    /// </summary>
    public static string Resolve(string template, PipelineExecutionContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return TokenPattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return ctx.Parameters.TryGetValue(key, out var val) ? val : match.Value;
        });
    }

    /// <summary>
    /// Resolves all token fields in an ActionConfig.
    /// Returns a copy with resolved strings (does not mutate original).
    /// </summary>
    public static ActionConfig ResolveAction(ActionConfig action, PipelineExecutionContext ctx) => new()
    {
        Type = action.Type,
        AgentName = Resolve(action.AgentName, ctx),
        Command = Resolve(action.Command, ctx),
        Parameters = Resolve(action.Parameters, ctx),
        Timeout = action.Timeout,
        PollInterval = action.PollInterval,
        FailAndContinue = action.FailAndContinue,
        IsReboot = action.IsReboot,
        Order = action.Order,
        CompletionCheckCommand = Resolve(action.CompletionCheckCommand, ctx),
        SecretEnv = Resolve(action.SecretEnv, ctx),
        CompletionPollIntervalSeconds = action.CompletionPollIntervalSeconds,
        UserName = Resolve(action.UserName, ctx),
        Password = Resolve(action.Password, ctx),
        From = Resolve(action.From, ctx),
        To = Resolve(action.To, ctx),
        Title = Resolve(action.Title, ctx),
        Body = Resolve(action.Body, ctx),
        Attachment = Resolve(action.Attachment, ctx),
        Embed = Resolve(action.Embed, ctx),
        LargeFilesShare = Resolve(action.LargeFilesShare, ctx),
    };

    /// <summary>
    /// Loads parameters from the trigger file.
    /// Supports two formats per line:
    ///   1. Comma-delimited: <c>_Key,Value</c>  (same format as parameter files)
    ///   2. Equals-delimited: <c>Key=Value</c>
    /// Lines starting with '#' are treated as comments.
    /// Keys with a leading underscore are stored both with and without the underscore
    /// so tokens like [BuildNumber] and [_BuildNumber] both resolve.
    /// </summary>
    public static void LoadTriggerFile(PipelineExecutionContext ctx, string triggerFilePath)
    {
        if (!File.Exists(triggerFilePath)) return;

        // A .json trigger must never reach ParseParameterFile: the CSV parser turns whole JSON
        // lines into key names, which is how a secret once ended up inside a key.
        if (IsLayeredConfig(triggerFilePath))
        {
            LoadJsonTrigger(ctx, triggerFilePath);
            return;
        }

        try
        {
            foreach (var (key, value) in ParseParameterFile(triggerFilePath))
                SetParameter(ctx, key, value, ParameterRank.TriggerFile);
        }
        catch (IOException)
        {
            // Trigger file may still be locked by writer — acceptable to skip silently
        }
    }

    /// <summary>
    /// Applies a layered JSON trigger. Every layer lands at <see cref="ParameterRank.TriggerFile"/>
    /// because the whole file describes this one run, unlike a config where the layers rank apart.
    /// Profiles are not applied: profile is an Initialize-node attribute and is unknown here.
    /// </summary>
    private static void LoadJsonTrigger(PipelineExecutionContext ctx, string filePath)
    {
        var config = ReadJsonConfig(filePath);
        if (config is null) return;

        foreach (var entry in config.Global)
            SetParameter(ctx, entry.Key, entry.Value, ParameterRank.TriggerFile);

        if (!string.IsNullOrEmpty(ctx.WatchItemTag)
            && config.Pipelines.TryGetValue(ctx.WatchItemTag, out var pinned))
        {
            foreach (var entry in pinned)
                SetParameter(ctx, entry.Key, entry.Value, ParameterRank.TriggerFile);
        }
    }
}
