using TestControllerGrpc.Ado.Reporting.Llm;

namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// Degradation LLM client (P25): returns null for every completion so HyDE and rerank fall back to their
/// deterministic paths when no real <see cref="ILlmClient"/> is configured by the host. Registered via
/// <c>TryAdd</c>, so a host that wires a real Azure OpenAI client always wins.
/// </summary>
public sealed class NullLlmClient : ILlmClient
{
    /// <inheritdoc />
    public Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
}
