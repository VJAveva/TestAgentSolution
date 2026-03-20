using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Core execution engine for the WatchList action pipeline.
/// Walks the action tree depth-first, executing ActionGroups, Actions,
/// Initialize, and Ref nodes with support for session tracking and retry.
/// </summary>
public interface IActionPipelineExecutor
{
    /// <summary>Raised for every action/event in the pipeline.</summary>
    event Action<PipelineLogEntry>? LogEntry;

    /// <summary>
    /// Raised when an IActionNode starts or finishes execution.
    /// Status: "Running", "Success", "Failed", "PartialFailure", "Cancelled".
    /// </summary>
    event Action<IActionNode, string>? NodeProgress;

    /// <summary>
    /// Raised when an action node fails, providing exit code and error details.
    /// </summary>
    event Action<IActionNode, int, string>? NodeFailed;

    /// <summary>
    /// Loads the template dictionary for Ref resolution.
    /// Called whenever the vocabulary is loaded/reloaded.
    /// </summary>
    void LoadTemplates(IEnumerable<TemplateConfig> templates);

    /// <summary>Executes an Event's children (the top-level entry point).</summary>
    Task ExecuteEventAsync(EventConfig ev, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>Executes an ActionGroup's children (public for direct execution from UI).</summary>
    Task<bool> ExecuteGroupAsync(ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>Executes a single Action node (public for direct execution from UI).</summary>
    Task<bool> ExecuteSingleActionAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Executes an Event's children with session tracking and snapshot isolation.
    /// </summary>
    Task ExecuteEventTrackedAsync(string watchItemTag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Re-executes only the actions that failed in a previous session,
    /// using the same resolved parameters from the original run.
    /// </summary>
    Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct);
}
