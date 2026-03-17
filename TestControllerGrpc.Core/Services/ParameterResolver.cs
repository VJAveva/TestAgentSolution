using System.IO;
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
    public static void LoadParameterFile(PipelineExecutionContext ctx, string parameterFilePath)
    {
        if (!File.Exists(parameterFilePath)) return;

        try
        {
            var entries = ParseParameterFile(parameterFilePath);
            foreach (var (key, value) in entries)
            {
                ctx.Parameters[key] = value;

                // Also store without leading underscore so [ControllerName] works
                if (key.StartsWith('_'))
                    ctx.Parameters[key[1..]] = value;
            }
        }
        catch { }
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
        File.WriteAllLines(filePath, lines);
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
            var entries = ParseParameterFile(triggerFilePath);
            foreach (var (key, value) in entries)
            {
                ctx.Parameters[key] = value;

                // Also store without leading underscore so [BuildNumber] works
                // when file has _BuildNumber,Value
                if (key.StartsWith('_'))
                    ctx.Parameters[key[1..]] = value;
            }
        }
        catch { /* trigger file may still be locked by writer */ }
    }
}
