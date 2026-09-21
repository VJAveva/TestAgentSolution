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
    /// Executes an ActionGroup's children with session tracking and snapshot isolation.
    /// </summary>
    Task<bool> ExecuteGroupTrackedAsync(string watchItemTag, ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Executes a single Action with session tracking.
    /// </summary>
    Task<bool> ExecuteSingleActionTrackedAsync(string watchItemTag, ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Executes a Template's children directly (a standalone, named action list) with session tracking,
    /// as if they were a sequential Event. Used to run a Template independently of any WatchItem.
    /// </summary>
    Task ExecuteTemplateTrackedAsync(string pipelineTag, TemplateConfig template, PipelineExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Re-executes only the actions that failed in a previous session,
    /// using the same resolved parameters from the original run.
    /// </summary>
    Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct);

    /// <summary>
    /// Runs one already-resolved node of a pipeline in isolation, dispatching to the same tracked
    /// entry points a full run uses so behaviour, sessions, live log and lock lifecycle are identical.
    /// </summary>
    /// <param name="watchItemTag">Pipeline the node belongs to; also the lock and RBAC resource id.</param>
    /// <param name="resolved">Node located by <see cref="NodeAddressing.TryResolve"/>.</param>
    /// <param name="initialize">
    /// Initialize node to load before the run, or null. Initialize only loads a parameter file — it
    /// dispatches no work — so it is applied to the context rather than executed as a pipeline step,
    /// which keeps the session's EventType (and therefore its label) describing the node the user picked.
    /// </param>
    /// <remarks>
    /// A default implementation so every existing executor and test fake keeps compiling unchanged.
    /// </remarks>
    Task<bool> ExecuteResolvedNodeAsync(
        string watchItemTag,
        ResolvedNode resolved,
        InitializeConfig? initialize,
        PipelineExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(ctx);

        if (initialize is not null)
        {
            var path = ParameterResolver.Resolve(initialize.ParameterFile, ctx);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ParameterResolver.TryLoadJsonConfig(ctx, path, initialize.Profile, ctx.WatchItemTag);
            else
                ParameterResolver.LoadParameterFile(ctx, path);
        }

        switch (resolved.Kind)
        {
            case RunnableNodeKind.Event:
                return RunEventAsync();

            case RunnableNodeKind.Group when resolved.Node is ActionGroupConfig group:
                return ExecuteGroupTrackedAsync(watchItemTag, group, ctx, ct);

            case RunnableNodeKind.Action when resolved.Node is ActionConfig action:
                return ExecuteSingleActionTrackedAsync(watchItemTag, action, ctx, ct);

            // A Ref is run through the group path so template lookup, expansion and failure
            // reporting stay in the executor's own ExecuteRefTracked rather than being duplicated here.
            case RunnableNodeKind.Template when resolved.Node is RefConfig refNode:
                return ExecuteGroupTrackedAsync(
                    watchItemTag,
                    new ActionGroupConfig
                    {
                        Tag = resolved.DisplayName,
                        ExecutionType = ExecutionMode.Sequential,
                        Children = [refNode],
                    },
                    ctx, ct);

            default:
                throw new InvalidOperationException(
                    $"Node '{resolved.Path}' of kind {resolved.Kind} cannot be run on its own.");
        }

        async Task<bool> RunEventAsync()
        {
            await ExecuteEventTrackedAsync(watchItemTag, resolved.OwningEvent, ctx, ct);
            return true;
        }
    }
}
