namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>One agent as the builder's picker sees it.</summary>
public sealed record BuilderAgent(string Name, string Status, bool IsOnline, bool IsBusy);

/// <summary>
/// The live agent roster, supplied by the host. An interface rather than a concrete registry so
/// the WPF host, the web host and the tests each hand the builder whatever they already have.
/// </summary>
public interface IBuilderAgentSource
{
    IReadOnlyList<BuilderAgent> GetAgents();
}
