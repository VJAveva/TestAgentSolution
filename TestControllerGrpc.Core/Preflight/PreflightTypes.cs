namespace TestControllerGrpc.Core.Preflight;

/// <summary>Outcome of one check. Error blocks the run; Warn asks; Pass is informational.</summary>
public enum PreflightStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>What is about to run, which decides how much of the pipeline is checked.</summary>
public enum PreflightScope
{
    /// <summary>The whole WatchItem, as a trigger file or a "Run pipeline" would.</summary>
    Pipeline,

    /// <summary>One group or action inside a pipeline.</summary>
    Node,

    /// <summary>A Templates-library node, borrowing a pipeline's settings.</summary>
    Template,

    /// <summary>A trigger file dropped in the watch folder - nobody is at the keyboard.</summary>
    TriggerFile,
}

/// <summary>The six groups, in report order.</summary>
public static class PreflightGroups
{
    public const string Settings = "Settings";
    public const string ControllerFiles = "Controller files";
    public const string InstallSources = "Install sources";
    public const string Agents = "Agents";
    public const string Structure = "Structure";
    public const string Disk = "Disk";

    public static readonly string[] InOrder =
        [Settings, ControllerFiles, InstallSources, Agents, Structure, Disk];
}

/// <summary>
/// One finding. <paramref name="FixHint"/> is written for whoever has to act on it at 2am, not for
/// the developer: say which file to edit or which folder to create.
/// </summary>
public sealed record PreflightCheck(
    string Group,
    string Name,
    PreflightStatus Status,
    string Detail,
    string? FixHint = null);

/// <summary>A token the run will use, with where its value came from.</summary>
public sealed record PreflightToken(string Token, string Value, string SourceLayer);

/// <summary>Everything the runner learned, grouped for display and for the result email.</summary>
public sealed class PreflightReport
{
    public required string Target { get; init; }
    public required PreflightScope Scope { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Elapsed { get; set; }

    public List<PreflightCheck> Checks { get; } = [];
    public List<PreflightToken> Tokens { get; } = [];

    public bool HasErrors => Checks.Any(c => c.Status == PreflightStatus.Fail);
    public bool HasWarnings => Checks.Any(c => c.Status == PreflightStatus.Warn);

    /// <summary>A run may start only when nothing failed. Warnings are the caller's decision.</summary>
    public bool CanRun => !HasErrors;

    public IEnumerable<PreflightCheck> Failures => Checks.Where(c => c.Status == PreflightStatus.Fail);
    public IEnumerable<PreflightCheck> Warnings => Checks.Where(c => c.Status == PreflightStatus.Warn);

    public int CountOf(PreflightStatus status) => Checks.Count(c => c.Status == status);

    public string Summary =>
        $"{CountOf(PreflightStatus.Fail)} failed, {CountOf(PreflightStatus.Warn)} warnings, {CountOf(PreflightStatus.Pass)} passed";

    /// <summary>Plain text for the log, the dialog and the [_EmailCheck] mail.</summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Pre-flight check - {Target}");
        sb.AppendLine($"{Summary} (in {Elapsed.TotalSeconds:F1}s)");
        sb.AppendLine();

        foreach (var group in PreflightGroups.InOrder)
        {
            var checks = Checks.Where(c => c.Group == group).ToList();
            if (checks.Count == 0) continue;

            sb.AppendLine($"== {group} ==");
            // Failures first: the reader needs the blocking reason before the noise.
            foreach (var check in checks.OrderBy(c => c.Status switch
                     {
                         PreflightStatus.Fail => 0,
                         PreflightStatus.Warn => 1,
                         _ => 2,
                     }))
            {
                sb.AppendLine($"  [{Label(check.Status)}] {check.Name}: {check.Detail}");
                if (!string.IsNullOrWhiteSpace(check.FixHint))
                    sb.AppendLine($"          Fix: {check.FixHint}");
            }
            sb.AppendLine();
        }

        if (Tokens.Count > 0)
        {
            sb.AppendLine("== Parameters this run will use ==");
            foreach (var token in Tokens.OrderBy(t => t.Token, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  [{token.Token}] = {token.Value}   ({token.SourceLayer})");
        }

        return TestControllerGrpc.Services.SecurityRedactor.Redact(sb.ToString()) ?? sb.ToString();
    }

    private static string Label(PreflightStatus status) => status switch
    {
        PreflightStatus.Fail => "FAIL",
        PreflightStatus.Warn => "WARN",
        _ => "PASS",
    };
}
