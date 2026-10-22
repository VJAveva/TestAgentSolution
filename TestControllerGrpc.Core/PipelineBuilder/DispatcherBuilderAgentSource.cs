using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// The builder's agent roster taken from the live gRPC dispatcher.
/// </summary>
/// <remarks>
/// Sits on <see cref="IAgentGrpcDispatcher"/> rather than the WebApi's <c>AgentRegistry</c> because
/// only the dispatcher exists in both hosts, so one adapter serves the WPF panel and the web path.
/// Health and busy are read exactly as <c>PreflightService.FactsFor</c> reads them, so the picker
/// and pre-flight never disagree about whether an agent is usable.
/// </remarks>
public sealed class DispatcherBuilderAgentSource(IAgentGrpcDispatcher dispatcher) : IBuilderAgentSource
{
    public IReadOnlyList<BuilderAgent> GetAgents()
    {
        var health = dispatcher.GetAllAgentHealth();

        return
        [
            .. dispatcher.RegisteredAgents
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    // No health record means the agent registered but has never answered - not online.
                    var online = health.TryGetValue(name, out var state) && state.IsHealthy;
                    var busy = dispatcher.IsAgentExecuting(name);
                    return new BuilderAgent(name, Describe(online, busy), online, busy);
                })
        ];
    }

    private static string Describe(bool online, bool busy)
        => !online ? "Offline" : busy ? "Busy" : "Online";
}
