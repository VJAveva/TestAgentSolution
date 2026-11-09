using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>Text for "Show resolved command" and "Open last output" (ContextMenu_Requirement.md 2.6, acceptance 8).</summary>
public static class RunInfoText
{
    /// <summary>
    /// The command as it would run. <paramref name="command"/> and <paramref name="parameters"/> arrive already
    /// token-resolved for display (secret tokens masked); this also redacts secrets typed in literally.
    /// </summary>
    public static string ResolvedCommand(string? agent, string? command, string? parameters)
    {
        var line = SecurityRedactor.RedactCommandLine(command ?? "", parameters ?? "");
        return $"Agent:   {(string.IsNullOrWhiteSpace(agent) ? "(controller)" : agent)}{Environment.NewLine}"
               + $"Command: {line}";
    }

    /// <summary>Summary of this action's result in the pipeline's last run, or why there is none.</summary>
    public static string LastResult(ActionExecutionResult? result, string actionTag, DateTime? sessionStartedUtc)
    {
        if (result is null)
            return $"'{actionTag}' has no result in the last run of this pipeline.";

        var lines = new List<string>
        {
            $"Action:   {actionTag}",
            $"Outcome:  {result.Outcome}",
            $"Exit code: {(result.ExitCode?.ToString() ?? "-")}",
            $"Duration: {result.Duration:hh\\:mm\\:ss}",
        };
        if (sessionStartedUtc is { } started) lines.Add($"Run started: {started.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) lines.Add($"Error:    {SecurityRedactor.Redact(result.ErrorMessage)}");
        lines.Add("The full output is in the Execution Log for that run.");
        return string.Join(Environment.NewLine, lines);
    }
}
