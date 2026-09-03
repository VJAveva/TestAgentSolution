namespace TestControllerGrpc.Services;

/// <summary>
/// Options for <see cref="AgentAuthInterceptor"/>. When <see cref="SharedSecret"/>
/// is null/empty, agent authentication is disabled (fail-open) so existing fleets
/// that have not yet been configured with a secret keep working; once a secret is
/// set the controller rejects unauthenticated agent RPCs.
/// </summary>
public sealed class AgentAuthOptions
{
    /// <summary>Metadata header the agent sends carrying the shared secret.</summary>
    public const string HeaderName = "x-agent-token";

    /// <summary>Shared secret matched against the inbound <c>x-agent-token</c> header.</summary>
    public string? SharedSecret { get; init; }
}
