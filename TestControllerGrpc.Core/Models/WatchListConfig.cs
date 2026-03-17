using System.Text.Json.Serialization;

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
}

// =============================================================================
// Execution session — tracks per-action results during a pipeline run
// =============================================================================
public sealed class ExecutionSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public string WatchItemTag { get; init; } = "";
    public string EventType { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public SessionState State { get; set; } = SessionState.Running;
    public List<ActionExecutionResult> ActionResults { get; } = [];

    /// <summary>Frozen context for retry — same tokens, same Initialize params.</summary>
    public Dictionary<string, string> ResolvedParameters { get; init; } = new();

    /// <summary>Frozen action tree for retry.</summary>
    public List<IActionNode> SnapshotNodes { get; init; } = [];

    public IEnumerable<ActionExecutionResult> FailedActions
        => ActionResults.Where(r => r.IsRetryable);

    public int TotalActions => ActionResults.Count;
    public int SucceededCount => ActionResults.Count(r => r.Outcome == ActionOutcome.Success);
    public int FailedCount => ActionResults.Count(r => r.IsRetryable);

    public string SummaryText => State switch
    {
        SessionState.Running => $"Running… ({SucceededCount}/{TotalActions} done)",
        SessionState.Completed => $"All {TotalActions} actions succeeded",
        SessionState.PartialFailure => $"{FailedCount} of {TotalActions} actions failed",
        SessionState.Failed => $"All {TotalActions} actions failed",
        _ => ""
    };
}

public enum SessionState { Running, Completed, PartialFailure, Failed }

public sealed class ActionExecutionResult
{
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
