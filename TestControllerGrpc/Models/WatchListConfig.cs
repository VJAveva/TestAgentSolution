using System.Xml.Serialization;

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
public interface IActionNode
{
    string NodeType { get; }
}

// =============================================================================
// <ActionGroup Tag="..." ExecutionType="Sequential|Parallel" FailAndContinue="true">
// =============================================================================
public sealed class ActionGroupConfig : IActionNode
{
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
    public string NodeType => "Action";
    public ActionType Type { get; set; } = ActionType.RunCommand;
    public string AgentName { get; set; } = "";
    public string Command { get; set; } = "";
    public string Parameters { get; set; } = "";
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
    public string NodeType => "Initialize";
    public string Tag { get; set; } = "";
    public string ParameterFile { get; set; } = "";
}

// =============================================================================
// <Ref TemplateID="UC152TCS" />
// =============================================================================
public sealed class RefConfig : IActionNode
{
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
