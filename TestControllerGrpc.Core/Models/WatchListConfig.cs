using System.Text.Json.Serialization;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Models;

// =============================================================================
// Root element: <WatchList>
// =============================================================================
public sealed class WatchListConfig
{
    public List<WatchItemConfig> WatchItems { get; set; } = new();
    public List<TemplateConfig> Templates { get; set; } = new();

    /// <summary>Path to the vocabulary XML file itself (for hot-reload).</summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// Non-fatal diagnostic messages produced during <c>WatchListXmlParser.Load</c>
    /// (e.g. deprecated attribute usage). UI hosts surface these via their
    /// log panel after a successful load. Empty for files that parse cleanly.
    /// </summary>
    public List<string> LoadWarnings { get; } = new();
}

// =============================================================================
// <WatchItem Tag="ConsolidatedBuild" Path="C:\ManualTrigger\" Filter="Consolidated.txt">
// =============================================================================
public sealed class WatchItemConfig
{
    public string Tag { get; set; } = "";
    public string Path { get; set; } = "";
    public string Filter { get; set; } = "*.*";
    public List<EventConfig> Events { get; set; } = new();

    /// <summary>Runtime: is this WatchItem currently enabled?</summary>
    public bool IsEnabled { get; set; } = true;

    // Trigger file metadata field names (configurable keys to extract from Filter file)
    public string BuildNumberField { get; set; } = "BuildNumber";
    public string DropLocationField { get; set; } = "DropLocation";

    /// <summary>Network base path for build browser (e.g., \\server\repl\Ado\SP\)</summary>
    public string BuildBasePath { get; set; } = "";

    // Runtime-only: populated when trigger fires (not serialized to XML)
    [System.Xml.Serialization.XmlIgnore]
    public string? LastBuildNumber { get; set; }
    [System.Xml.Serialization.XmlIgnore]
    public string? LastDropLocation { get; set; }
}

// =============================================================================
// <Event Type="Renamed" ExecutionType="Sequential">
// =============================================================================
public sealed class EventConfig
{
    public string Type { get; set; } = "Renamed";         // Renamed, Created, Changed
    public ExecutionMode ExecutionType { get; set; } = ExecutionMode.Sequential;
    public List<IActionNode> Children { get; set; } = new();
}

// =============================================================================
// Polymorphic children: ActionGroup | Action | Initialize | Ref
// =============================================================================
[JsonPolymorphic(TypeDiscriminatorPropertyName = "nodeType")]
[JsonDerivedType(typeof(ActionGroupConfig), "ActionGroup")]
[JsonDerivedType(typeof(ActionConfig), "Action")]
[JsonDerivedType(typeof(InitializeConfig), "Initialize")]
[JsonDerivedType(typeof(RefConfig), "Ref")]
public interface IActionNode
{
    [JsonIgnore]
    string NodeType { get; }
}

// =============================================================================
// <ActionGroup Tag="..." ExecutionType="Sequential|Parallel" FailAndContinue="true">
// =============================================================================
public sealed class ActionGroupConfig : IActionNode
{
    [JsonIgnore]
    public string NodeType => "ActionGroup";
    public string Tag { get; set; } = "";
    public ExecutionMode ExecutionType { get; set; } = ExecutionMode.Sequential;
    public bool FailAndContinue { get; set; }
    public List<IActionNode> Children { get; set; } = new();
}

// =============================================================================
// <Action Type="RunCommand|RunRemoteCommand|SendMail" ... />
// =============================================================================
public sealed class ActionConfig : IActionNode
{
    [JsonIgnore]
    public string NodeType => "Action";
    public ActionType Type { get; set; } = ActionType.RunCommand;
    public string AgentName { get; set; } = "";
    public string Command { get; set; } = "";
    public string Parameters { get; set; } = "";
    /// <summary>Timeout in seconds. 0 = no timeout (infinite). For installs, use 3600+ (1 hour).</summary>
    public int Timeout { get; set; }
    public int PollInterval { get; set; } = 1000;
    public bool FailAndContinue { get; set; }
    public bool IsReboot { get; set; }
    public string Order { get; set; } = "";

    /// <summary>
    /// Human-readable tag for this action, displayed in the Pipeline view as the
    /// action pill label. Resolution order: Tag → Order → generated short label.
    /// Example: "Install WSP", "Copy files", "Reboot".
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Returns the best available display tag for this action.
    /// Resolution: explicit Tag → explicit Order → generated short label from Command/Type.
    /// </summary>
    [JsonIgnore]
    public string ResolvedTag
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Tag)) return Tag;
            if (!string.IsNullOrWhiteSpace(Order)) return Order;
            return GenerateShortLabel();
        }
    }

    private string GenerateShortLabel()
    {
        if (Type == ActionType.SendMail)
            return !string.IsNullOrEmpty(To) ? $"Email: {To.Split(',')[0].Trim()}" : "SendMail";

        var cmd = Command;
        if (string.IsNullOrEmpty(cmd)) return Type.ToString();

        // Extract filename without extension from path-like commands
        if (cmd.Contains('\\') || cmd.Contains('/'))
            cmd = System.IO.Path.GetFileNameWithoutExtension(cmd);

        // Trim to reasonable length
        if (cmd.Length > 24)
            cmd = cmd[..22] + "..";

        return cmd;
    }

    /// <summary>
    /// Optional command to run after the main process exits to check if child processes (e.g. msiexec)
    /// have completed. The poll loop runs until this command returns exit code 1 or its output contains "DONE".
    /// Example: <c>cmd /c tasklist | findstr msiexec || echo DONE</c>
    /// </summary>
    public string CompletionCheckCommand { get; set; } = "";

    /// <summary>
    /// How often (in seconds) to run the CompletionCheckCommand. Default is 30 seconds.
    /// </summary>
    public int CompletionPollIntervalSeconds { get; set; } = 30;

    // Credentials (RunRemoteCommand)
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";

    // SendMail
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Attachment { get; set; } = "";
    public string Embed { get; set; } = "";
    public string LargeFilesShare { get; set; } = "";

    // Smart Retry
    /// <summary>Max retry attempts after initial failure. 0 = no retry (default).</summary>
    public int MaxRetries { get; set; }

    /// <summary>Initial delay in seconds before first retry. Default: 10.</summary>
    public int RetryDelaySeconds { get; set; } = 10;

    /// <summary>Backoff strategy: "Fixed" = same delay each time, "Exponential" = delay doubles. Default: Exponential.</summary>
    public string RetryBackoff { get; set; } = "Exponential";

    /// <summary>
    /// Comma-separated exit codes that trigger retry. Empty = retry on any non-zero exit.
    /// Example: "-1,1,2" retries only on those exit codes.
    /// </summary>
    public string RetryOnExitCodes { get; set; } = "";
}

// =============================================================================
// <Initialize Tag="InitializeParams" ParameterFile="C:\...\Emails.txt" />
// =============================================================================
public sealed class InitializeConfig : IActionNode
{
    [JsonIgnore]
    public string NodeType => "Initialize";
    public string Tag { get; set; } = "";
    public string ParameterFile { get; set; } = "";
}

// =============================================================================
// <Ref TemplateID="UC152TCS" />
// =============================================================================
public sealed class RefConfig : IActionNode
{
    [JsonIgnore]
    public string NodeType => "Ref";
    public string TemplateID { get; set; } = "";
}

// =============================================================================
// <Template ID="UC152ExecuteTCS"> ... </Template>
// =============================================================================
public sealed class TemplateConfig
{
    public string ID { get; set; } = "";
    public List<IActionNode> Children { get; set; } = new();
}

// =============================================================================
// Enumerations
// =============================================================================
public enum ExecutionMode
{
    Sequential,
    Parallel
}

public enum ActionType
{
    RunCommand,
    RunRemoteCommand,
    SendMail
}

// =============================================================================
// Runtime execution context — carries token parameters during pipeline run
// =============================================================================
public sealed class PipelineExecutionContext
{
    public string WatchItemPath { get; set; } = "";
    public string TriggerFileName { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CancellationToken CancellationToken { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Short session ID for log prefixing in concurrent execution.</summary>
    public string SessionId { get; set; } = "";
}

// =============================================================================
// Execution session — tracks per-action results during a pipeline run
// =============================================================================
public sealed class ExecutionSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string WatchItemTag { get; init; } = "";
    public string EventType { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public SessionState State { get; set; } = SessionState.Running;

    /// <summary>Who triggered this session.</summary>
    public string UserId { get; set; } = "";

    /// <summary>"WebClient" or "WPF".</summary>
    public string Source { get; set; } = "";

    /// <summary>Agents locked by this session (resolved names).</summary>
    public string[] LockedAgents { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Cancellation token source for this session. Call <see cref="RequestCancellation"/>
    /// to signal the running pipeline to stop. The token is passed through to the executor.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Signals cancellation to the running pipeline.</summary>
    public void RequestCancellation() => Cts.Cancel();

    // GAP 10 fix: Thread-safe collection for parallel action groups.
    // Parallel ExecuteChildrenAsync calls RecordResult from multiple threads
    // simultaneously. A plain List<T>.Add is not thread-safe and can corrupt
    // data (lost items, IndexOutOfRangeException). ConcurrentBag<T> is
    // lock-free for concurrent Add and safe for enumeration (snapshot).
    private readonly System.Collections.Concurrent.ConcurrentBag<ActionExecutionResult> _actionResults = new();

    /// <summary>Thread-safe access to all recorded action results.</summary>
    public IReadOnlyCollection<ActionExecutionResult> ActionResults => _actionResults;

    /// <summary>Thread-safe: adds a result from any thread during parallel execution.</summary>
    public void AddResult(ActionExecutionResult result) => _actionResults.Add(result);

    /// <summary>Frozen context for retry — same tokens, same Initialize params.</summary>
    public Dictionary<string, string> ResolvedParameters { get; init; } = new();

    /// <summary>Frozen action tree for retry.</summary>
    public List<IActionNode> SnapshotNodes { get; init; } = [];

    public IEnumerable<ActionExecutionResult> FailedActions
        => _actionResults.Where(r => r.IsRetryable);

    public int TotalActions => _actionResults.Count;
    public int SucceededCount => _actionResults.Count(r => r.Outcome == ActionOutcome.Success);
    public int FailedCount => _actionResults.Count(r => r.IsRetryable);

    public string SummaryText => State switch
    {
        SessionState.Running => $"Running… ({SucceededCount}/{TotalActions} done)",
        SessionState.Completed => $"All {TotalActions} actions succeeded",
        SessionState.PartialFailure => $"{FailedCount} of {TotalActions} actions failed",
        SessionState.Failed => $"All {TotalActions} actions failed",
        _ => ""
    };

    // ── Per-agent tracking for dashboard ────────────────────────────
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentSessionSummary>
        _agentSummaries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Track a per-agent action result for dashboard rendering.</summary>
    public void TrackAgentAction(ActionExecutionResult result)
    {
        var agentName = result.AgentName ?? "Controller";
        _agentSummaries.AddOrUpdate(
            agentName,
            _ => new AgentSessionSummary
            {
                AgentName = agentName,
                Actions = new System.Collections.Concurrent.ConcurrentBag<ActionExecutionResult> { result },
            },
            (_, existing) =>
            {
                existing.Actions.Add(result);
                return existing;
            });
    }

    /// <summary>Returns per-agent summaries for dashboard API.</summary>
    public IReadOnlyList<AgentSessionSummary> GetAgentSummaries()
        => _agentSummaries.Values.ToList();

    // ── Per-session log buffer for reconnection backfill ─────────────
    private readonly List<PipelineLogEntry> _logBuffer = new(500);
    private readonly object _logLock = new();

    /// <summary>Buffer a log entry for this session (used for reconnection backfill).</summary>
    public void AddLogEntry(PipelineLogEntry entry)
    {
        lock (_logLock)
        {
            _logBuffer.Add(entry);
            if (_logBuffer.Count > 500)
                _logBuffer.RemoveRange(0, 100);
        }
    }

    /// <summary>Returns recent buffered log entries for backfill.</summary>
    public IReadOnlyList<PipelineLogEntry> GetRecentLogs(int count)
    {
        lock (_logLock)
        {
            if (count <= 0) return _logBuffer.ToList();
            return _logBuffer.TakeLast(count).ToList();
        }
    }
}

public enum SessionState { Running, Completed, PartialFailure, Failed }

public sealed class ActionExecutionResult
{
    private static long _sequenceCounter;

    /// <summary>
    /// Monotonically-increasing creation order. Used by the dashboard to render
    /// pills in a stable, deterministic sequence even though they are stored
    /// in a <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>
    /// (which has no defined enumeration order).
    /// </summary>
    public long Sequence { get; init; } = System.Threading.Interlocked.Increment(ref _sequenceCounter);

    public string ActionTag { get; init; } = "";
    public string ActionType { get; init; } = "";
    public string? AgentName { get; init; }
    public string Command { get; init; } = "";
    public ActionOutcome Outcome { get; set; } = ActionOutcome.Unknown;
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>For retry: the original frozen node.</summary>
    public IActionNode? OriginalNode { get; init; }

    public bool IsRetryable => Outcome is ActionOutcome.Failed
        or ActionOutcome.Terminated or ActionOutcome.TimedOut;

    public string StatusIcon => Outcome switch
    {
        ActionOutcome.Success => "✓",
        ActionOutcome.Failed => "✗",
        ActionOutcome.Terminated => "⊘",
        ActionOutcome.TimedOut => "⏱",
        _ => "…"
    };

    public string DurationText => Duration.TotalSeconds < 1
        ? $"{Duration.TotalMilliseconds:F0}ms"
        : Duration.TotalMinutes < 1
            ? $"{Duration.TotalSeconds:F1}s"
            : Duration.ToString(@"mm\:ss");
}

public enum ActionOutcome { Unknown, Success, Failed, Terminated, TimedOut }

/// <summary>
/// Well-known parameter keys used across the WatchList pipeline.
/// Centralized so dashboards, log writers, and the file watcher can't
/// silently disagree on the spelling.
/// </summary>
public static class WatchListConstants
{
    /// <summary>Underscore-prefixed key written by the file watcher so token
    /// substitution works as <c>[BuildNumber]</c>. The dashboard reads from
    /// this key when displaying the build number on a session card.</summary>
    public const string BuildNumberKey = "_BuildNumber";
}

/// <summary>Per-agent execution summary for dashboard rendering.</summary>
public sealed class AgentSessionSummary
{
    public string AgentName { get; set; } = "";
    public string Status => Actions.Any(a => a.Outcome == ActionOutcome.Failed) ? "Failed"
        : Actions.All(a => a.Outcome == ActionOutcome.Success) && Actions.Count > 0 ? "Success"
        : "Executing";
    public System.Collections.Concurrent.ConcurrentBag<ActionExecutionResult> Actions { get; set; } = new();
    public int CompletedCount => Actions.Count(a => a.Outcome != ActionOutcome.Unknown);
    public int TotalCount => Actions.Count;
}

/// <summary>
/// Documents common Windows/MSI/PowerShell exit codes
/// for diagnostic display in the execution log.
/// </summary>
public static class ExitCodeReference
{
    public static string Describe(int code) => code switch
    {
        0 => "Success",
        1 => "General error",
        2 => "File not found",
        3 => "Path not found",
        5 => "Access denied",
        259 => "Process still running (timeout or waiting for input)",
        1603 => "MSI install fatal error",
        1618 => "Another MSI install in progress",
        1641 => "MSI: reboot initiated",
        3010 => "MSI: reboot required to complete",
        -1 => "Abnormal termination",
        -1073741510 => "Process killed (Ctrl+C or TaskKill)",
        -1073741819 => "Access violation (crash)",
        -532462766 => ".NET unhandled exception",
        _ => $"Unknown exit code {code}"
    };
}
