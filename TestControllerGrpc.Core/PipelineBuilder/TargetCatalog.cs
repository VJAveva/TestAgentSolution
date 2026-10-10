using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// One buildable target: a single per-release <c>pipeline-config.json</c> and the paths it defines.
/// </summary>
/// <remarks>
/// Keyed by <see cref="Id"/> (the config file name), NOT by release: the live fleet has two configs
/// both naming release <c>SP2026R2</c> - one per use case - so release name is not unique.
/// </remarks>
public sealed record TargetDescriptor(
    string Id,
    string ReleaseName,
    string ConfigPath,
    IReadOnlyList<string> Profiles,
    IReadOnlyDictionary<string, string> Values);

/// <summary>Targets found, plus any file that looked like a config but could not be read.</summary>
public sealed record TargetCatalogResult(
    IReadOnlyList<TargetDescriptor> Targets,
    IReadOnlyList<string> Problems);

public interface ITargetCatalog
{
    TargetCatalogResult GetTargets();
}

/// <summary>
/// Derives the release/target catalog from the per-release <c>pipeline-config.json</c> files that
/// already exist, rather than from a new hand-authored catalog file.
/// </summary>
/// <remarks>
/// A separate catalog file would be a second source of truth for the same installer and binaries
/// paths, and it would drift from the configs the executor actually reads. Deriving means adding a
/// release is dropping a config in the folder - no code change, nothing to keep in step.
/// </remarks>
public sealed class DerivedTargetCatalog(string parametersRoot) : ITargetCatalog
{
    /// <summary>
    /// Keys that describe the ENVIRONMENT or the SELECTED BUILD rather than the target, so they are
    /// never carried into a generated config: the build comes from GlobalVariables/trigger and the
    /// constants come from GlobalVariables. Everything else a team puts in the global layer IS part
    /// of the target - a deny-list keeps a team's new path key working without a code change.
    /// <para>
    /// Also read by the builder's validation as the set of tokens that are legitimately unresolved
    /// at authoring time, so the two definitions cannot drift apart.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> SuppliedAtRunTime = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "_BuildNumber", "_DropLocation", "_ControllerName", "_EmailCheck",
    };

    public TargetCatalogResult GetTargets()
    {
        var targets = new List<TargetDescriptor>();
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(parametersRoot) || !Directory.Exists(parametersRoot))
            return new TargetCatalogResult(targets, problems);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(parametersRoot, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TargetCatalogResult(targets, [$"'{parametersRoot}' could not be listed: {ex.Message}"]);
        }

        foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var config = ParameterResolver.ReadJsonConfig(path);
            if (config is null)
            {
                problems.Add($"'{Path.GetFileName(path)}' could not be read as JSON.");
                continue;
            }

            // No release name means it is not a per-release config - GlobalVariables.json lands here.
            if (!config.Global.TryGetValue("_ReleaseName", out var release) || string.IsNullOrWhiteSpace(release))
                continue;

            var values = config.Global
                .Where(e => !SuppliedAtRunTime.Contains(e.Key))
                .Where(e => !TokenDisplay.IsSecret(e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

            targets.Add(new TargetDescriptor(
                Id: Path.GetFileNameWithoutExtension(path),
                ReleaseName: release,
                ConfigPath: path,
                Profiles: [.. config.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)],
                Values: values));
        }

        return new TargetCatalogResult(targets, problems);
    }
}
