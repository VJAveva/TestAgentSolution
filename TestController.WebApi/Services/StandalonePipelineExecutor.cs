using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Stub implementation of <see cref="IActionPipelineExecutor"/> for standalone WebApi mode.
/// The standalone WebApi can create sessions and track status but delegates actual pipeline
/// execution to the WPF-hosted controller (or runs nothing in standalone-only deployments).
/// </summary>
public sealed class StandalonePipelineExecutor : IActionPipelineExecutor
{
    public event Action<PipelineLogEntry>? LogEntry;
    public event Action<IActionNode, string>? NodeProgress;
    public event Action<IActionNode, int, string>? NodeFailed;

    public void LoadTemplates(IEnumerable<TemplateConfig> templates) { }

    public Task ExecuteEventAsync(EventConfig ev, PipelineExecutionContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public Task<bool> ExecuteGroupAsync(ActionGroupConfig group, PipelineExecutionContext ctx, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> ExecuteSingleActionAsync(ActionConfig action, PipelineExecutionContext ctx, CancellationToken ct)
        => Task.FromResult(false);

    public Task ExecuteEventTrackedAsync(string watchItemTag, EventConfig evt, PipelineExecutionContext ctx, CancellationToken ct)
    {
        LogEntry?.Invoke(new PipelineLogEntry(DateTime.Now, "Standalone",
            $"Pipeline execution not available in standalone WebApi mode for '{watchItemTag}'"));
        return Task.CompletedTask;
    }

    public Task RetryFailedAsync(ExecutionSession previousSession, CancellationToken ct)
        => Task.CompletedTask;
}
