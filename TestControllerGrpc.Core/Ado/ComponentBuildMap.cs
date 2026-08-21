using System.Text.Json;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>One component's build coordinates + regression scope — the modern equivalent of a <c>vobs.csv</c> row.</summary>
public sealed record ComponentBuildInfo(
    string ComponentId,
    int BuildDefinitionId,
    int LogId,
    string? Repository,
    IReadOnlyList<string> RegressionAreas,
    IReadOnlyList<string> UseCases);

/// <summary>
/// Maps a consumed-component name (from a build manifest, e.g. "AppServer.AASysObjects") to its build
/// definition id + log id, so the collector can recurse into that component's OMI build. Loaded from the
/// optional <c>buildDefinitionId</c>/<c>logId</c>/<c>repository</c> fields on each node in
/// docs/impact/component-usecase-map.v1.json. Components without those fields simply aren't recursable yet.
/// </summary>
public interface IComponentBuildMap
{
    /// <summary>Resolves by manifest component name via substring match on component id/aliases (legacy behaviour).</summary>
    ComponentBuildInfo? Resolve(string manifestComponentName);

    /// <summary>All recursable components (those with a build definition id) — for component-centric collection.</summary>
    IReadOnlyList<ComponentBuildInfo> All();
}

public sealed class ComponentBuildMap : IComponentBuildMap
{
    private readonly IAppLogger _logger;
    private readonly List<ComponentBuildInfo> _entries = [];

    public ComponentBuildMap(IAppLogger logger)
    {
        _logger = logger;
        Load(ComponentMapLocator.FindMapPath());
    }

    public IReadOnlyList<ComponentBuildInfo> All() => _entries;

    public ComponentBuildInfo? Resolve(string manifestComponentName)
    {
        if (string.IsNullOrWhiteSpace(manifestComponentName))
            return null;

        // Legacy match: the changed manifest key CONTAINS the component id (e.g. "AppServer.AASysObjects"
        // contains "AASysObjects"). Most-specific (longest) name wins so "Security" doesn't shadow "Cybersecurity".
        return _entries
            .OrderByDescending(e => e.ComponentId.Length)
            .FirstOrDefault(e => manifestComponentName.Contains(e.ComponentId, StringComparison.OrdinalIgnoreCase));
    }

    private void Load(string mapPath)
    {
        if (!File.Exists(mapPath))
        {
            _logger.Warn("Ado", $"ComponentBuildMap: map not found at '{mapPath}'. OMI recursion disabled until buildDefinitionId fields are added.");
            return;
        }

        try
        {
            using var stream = File.OpenRead(mapPath);
            using var doc = JsonDocument.Parse(stream);

            // Preferred source: the "vobs" array (the faithful legacy vobs.csv: name -> buildDef -> logId).
            if (doc.RootElement.TryGetProperty("vobs", out var vobs) && vobs.ValueKind == JsonValueKind.Array)
            {
                foreach (var vob in vobs.EnumerateArray())
                {
                    var name = vob.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!vob.TryGetProperty("buildDef", out var defEl) || defEl.ValueKind != JsonValueKind.Number)
                        continue;

                    var logId = vob.TryGetProperty("logId", out var logEl) && logEl.ValueKind == JsonValueKind.Number
                        ? logEl.GetInt32()
                        : 0;
                    var repo = vob.TryGetProperty("repository", out var repoEl) && repoEl.ValueKind == JsonValueKind.String
                        ? repoEl.GetString()
                        : null;

                    _entries.Add(new ComponentBuildInfo(name, defEl.GetInt32(), logId, repo, ReadStrings(vob, "regressionAreas"), ReadStrings(vob, "useCases")));
                }
            }

            // Fallback: any component node carrying an inline buildDefinitionId.
            if (doc.RootElement.TryGetProperty("components", out var components))
            {
                foreach (var component in components.EnumerateArray())
                {
                    var id = component.GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!component.TryGetProperty("buildDefinitionId", out var defEl) || defEl.ValueKind != JsonValueKind.Number)
                        continue; // not recursable without a definition id

                    var logId = component.TryGetProperty("logId", out var logEl) && logEl.ValueKind == JsonValueKind.Number
                        ? logEl.GetInt32()
                        : 0;
                    var repo = component.TryGetProperty("repository", out var repoEl) && repoEl.ValueKind == JsonValueKind.String
                        ? repoEl.GetString()
                        : null;

                    _entries.Add(new ComponentBuildInfo(id, defEl.GetInt32(), logId, repo, ReadStrings(component, "regressionAreas"), ReadStrings(component, "useCases")));
                }
            }

            _logger.Info("Ado", $"ComponentBuildMap loaded {_entries.Count} recursable components.");
        }
        catch (Exception ex)
        {
            _logger.Error("Ado", $"ComponentBuildMap: failed to parse '{mapPath}'.", ex);
        }
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}

/// <summary>Shared lookup for the component map file, walking up from the executing assembly.</summary>
internal static class ComponentMapLocator
{
    public static string FindMapPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "impact", "component-usecase-map.v1.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "docs", "impact", "component-usecase-map.v1.json");
    }
}
