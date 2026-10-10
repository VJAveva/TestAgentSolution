namespace TestControllerGrpc.Models;

/// <summary>
/// The one shared <c>GlobalVariables.json</c>: a single build selected once and used by every
/// pipeline of the matching release. Flat by design - unlike
/// <see cref="PipelineParameterConfig"/> it has no global/profile/pipeline layers, because there is
/// only ever one of it and it is never per-pipeline.
/// </summary>
public sealed class GlobalVariablesConfig
{
    /// <summary>Where the controller keeps the file. Hosts opt in by pointing the resolver here.</summary>
    public const string DefaultPath = @"C:\TestControllerService\Parameters\GlobalVariables.json";

    /// <summary>
    /// Keys that say WHICH BUILD is selected, and therefore the only ones the release guard gates.
    /// Pushing an SP2026 build into an SP2023R2SP2 pipeline would install the wrong product, so
    /// these three are applied only on an exact release match.
    /// </summary>
    public static readonly string[] ReleaseScopedKeys =
        ["_ReleaseName", "_BuildNumber", "_DropLocation"];

    public int Version { get; set; } = 1;

    /// <summary>Every key in the file except <c>Version</c>.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Release this build belongs to, or empty when the file does not say.</summary>
    public string ReleaseName => Get("_ReleaseName");

    public string BuildNumber => Get("_BuildNumber");

    public string DropLocation => Get("_DropLocation");

    /// <summary>True when the key names one of the build fields, with or without the underscore.</summary>
    public static bool IsReleaseScoped(string key) =>
        ReleaseScopedKeys.Contains("_" + key.TrimStart('_'), StringComparer.OrdinalIgnoreCase);

    private string Get(string key) => Values.TryGetValue(key, out var v) ? v : "";
}
