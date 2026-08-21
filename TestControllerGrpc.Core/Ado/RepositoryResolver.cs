using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Resolves a raw file-path hint (e.g. first path segment "SysObj/Scheduler/Scheduler.cpp") to a
/// canonical component id from docs/impact/component-usecase-map.v1.json.
/// Resolution order (Copilot-Prompts-AdoIntegration.md Phase D1): exact id match -> "AppServer."+hint
/// -> known-alias dict -> unresolved (logged, returns null). Never guesses.
/// </summary>
public interface IRepositoryResolver
{
    string? ResolveComponent(string pathHint);
}

public sealed class RepositoryResolver : IRepositoryResolver
{
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, string> _byIdOrAlias;

    public RepositoryResolver(IAppLogger logger, IOptions<AdoOptions> options)
    {
        _logger = logger;
        _byIdOrAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadMap(FindMapPath());
    }

    public string? ResolveComponent(string pathHint)
    {
        if (string.IsNullOrWhiteSpace(pathHint))
            return null;

        if (_byIdOrAlias.TryGetValue(pathHint, out var exact))
            return exact;

        var withPrefix = $"AppServer.{pathHint}";
        if (_byIdOrAlias.TryGetValue(withPrefix, out var prefixed))
            return prefixed;

        _logger.Warn("Ado", $"RepositoryResolver: could not resolve component hint '{pathHint}' \u2014 leaving unmapped rather than guessing (ADR-06).");
        return null;
    }

    private void LoadMap(string mapPath)
    {
        if (!File.Exists(mapPath))
        {
            _logger.Warn("Ado", $"RepositoryResolver: component map not found at '{mapPath}'. All resolutions will fail until it exists.");
            return;
        }

        try
        {
            using var stream = File.OpenRead(mapPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("components", out var components))
                return;

            foreach (var component in components.EnumerateArray())
            {
                var id = component.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                _byIdOrAlias[id] = id;

                if (component.TryGetProperty("aliases", out var aliases))
                {
                    foreach (var alias in aliases.EnumerateArray())
                    {
                        var aliasText = alias.GetString();
                        if (!string.IsNullOrWhiteSpace(aliasText))
                            _byIdOrAlias[aliasText] = id;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Ado", $"RepositoryResolver: failed to parse component map at '{mapPath}'.", ex);
        }
    }

    private static string FindMapPath()
    {
        // Walk up from the executing assembly looking for docs/impact/component-usecase-map.v1.json,
        // since the WPF host and WebApi host have different base directories.
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
