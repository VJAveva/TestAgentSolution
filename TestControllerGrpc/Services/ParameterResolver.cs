using System.IO;
using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Handles [Token] parameter resolution for the action pipeline.
///
/// Tokens like [BuildNumber], [EmailAddress], [DropLocation] are replaced
/// from two sources:
///   1. The trigger file content (first line = BuildNumber by convention)
///   2. Initialize → ParameterFile (key=value lines, e.g. Emails.txt)
///
/// This mirrors the legacy TestControllerSvc behavior.
/// </summary>
public static partial class ParameterResolver
{
    private static readonly Regex TokenPattern = TokenRegex();

    [GeneratedRegex(@"\[(\w+)\]")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Loads parameters from the trigger file. By convention, the first
    /// non-empty line is used as [BuildNumber].
    /// </summary>
    public static void LoadTriggerFile(PipelineExecutionContext ctx, string triggerFilePath)
    {
        if (!File.Exists(triggerFilePath)) return;

        try
        {
            var lines = File.ReadAllLines(triggerFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            if (lines.Length > 0)
                ctx.Parameters["BuildNumber"] = lines[0].Trim();

            // Additional lines as key=value pairs
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                    ctx.Parameters[parts[0].Trim()] = parts[1].Trim();
            }
        }
        catch { /* trigger file may still be locked by writer */ }
    }

    /// <summary>
    /// Loads parameters from an Initialize ParameterFile (key=value per line).
    /// Merges into the existing context parameters.
    /// </summary>
    public static void LoadParameterFile(PipelineExecutionContext ctx, string parameterFilePath)
    {
        if (!File.Exists(parameterFilePath)) return;

        try
        {
            foreach (var line in File.ReadAllLines(parameterFilePath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                    ctx.Parameters[parts[0].Trim()] = parts[1].Trim();
            }
        }
        catch { }
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
}
