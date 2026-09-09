namespace TestControllerGrpc.Models;

/// <summary>
/// Layered parameter configuration. Replaces the per-stage .txt files that duplicated most of
/// their keys: <c>global</c> holds what every pipeline shares, <c>profiles</c> holds only what
/// genuinely differs per stage (the agent lists), and <c>pipelines</c> holds a build pinned to a
/// single WatchItem tag. Each layer is applied at its own <see cref="Services.ParameterRank"/>.
/// </summary>
public sealed class PipelineParameterConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Defaults every pipeline inherits.</summary>
    public Dictionary<string, string> Global { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Named stage overlays, keyed by profile name (e.g. "Warm", "Sanity").</summary>
    public Dictionary<string, Dictionary<string, string>> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-pipeline overrides, keyed by WatchItem Tag.</summary>
    public Dictionary<string, Dictionary<string, string>> Pipelines { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
