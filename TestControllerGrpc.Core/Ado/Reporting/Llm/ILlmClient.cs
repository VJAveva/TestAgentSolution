namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>One chat message in an LLM request.</summary>
public sealed record LlmMessage(string Role, string Content);

/// <summary>A provider-agnostic chat-completion request.</summary>
public sealed record LlmRequest(
    string Model,
    IReadOnlyList<LlmMessage> Messages,
    int MaxOutputTokens = 800,
    double Temperature = 0,
    bool JsonOutput = false);

/// <summary>
/// Minimal provider-agnostic chat-completion client. Implemented by <see cref="AzureOpenAiClient"/>;
/// swap the implementation to target OpenAI, a gateway, or an on-prem model without touching callers.
/// </summary>
public interface ILlmClient
{
    /// <summary>Returns the assistant message content, or null when the call fails or is not configured.</summary>
    Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct);
}
