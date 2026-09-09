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
    public static void SaveParameterFile(string filePath, IEnumerable<(string Key, string Value)> entries)
    {
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
        var config = ReadJsonConfig(filePath);
        if (config is null) return;

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
}
