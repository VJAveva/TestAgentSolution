using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Async, LLM-backed summarization of code changes. Sits in front of the deterministic
/// <see cref="IChurnSummarizer"/> (used as the fallback) and is resolved by callers that want the
/// richer, diff-grounded narrative on demand. When the LLM is disabled or fails, the offline
/// summary is returned instead so the UI always gets a result.
/// </summary>
public interface ILlmChangeSummarizer
{
    /// <summary>Diff-grounded summary for a single component (individual-component view).</summary>
    Task<string> SummarizeComponentAsync(SubsystemRow row, CancellationToken ct);

    /// <summary>Cross-component release digest (map per component, then reduce).</summary>
    Task<ChurnSummary> SummarizeReleaseAsync(ChurnReport report, CancellationToken ct);
}
