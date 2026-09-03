using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Fallback <see cref="ILlmChangeSummarizer"/> used when the LLM is disabled/unconfigured: simply
/// wraps the deterministic <see cref="IChurnSummarizer"/> so callers get the offline summary with no
/// external call and no cost.
/// </summary>
public sealed class OfflineChangeSummarizer : ILlmChangeSummarizer
{
    private readonly IChurnSummarizer _inner;

    public OfflineChangeSummarizer(IChurnSummarizer inner) => _inner = inner;

    public Task<string> SummarizeComponentAsync(SubsystemRow row, CancellationToken ct) =>
        Task.FromResult(_inner.SummarizeComponent(row));

    public Task<ChurnSummary> SummarizeReleaseAsync(ChurnReport report, CancellationToken ct) =>
        Task.FromResult(_inner.Summarize(report));
}
