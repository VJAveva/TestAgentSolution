using TestControllerGrpc.Models;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// What the REST surface needs from the host that owns the Parameters folder and the WatchList.
/// Registered only by the controller app; a host without it answers 404.
/// </summary>
public sealed record PipelineBuilderHost(
    PipelineBuilderService Builder,
    IBuilderAgentSource Agents,
    Func<WatchListConfig> LiveConfig,
    string FragmentDir);
