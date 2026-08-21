using System.Xml.Linq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>Classifies a component as Runtime/Config/Both from docs/impact/component-categories.xml.</summary>
public interface IComponentCategorizer
{
    RegressionCategoryKind Categorize(string component);

    /// <summary>True when a classification exists for the component (so confidence can be Declared vs Assumed).</summary>
    bool IsClassified(string component);
}

public sealed class ComponentCategorizer : IComponentCategorizer
{
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, RegressionCategoryKind> _map = new(StringComparer.OrdinalIgnoreCase);

    public ComponentCategorizer(IAppLogger logger)
    {
        _logger = logger;
        Load(FindCategoriesPath());
    }

    public RegressionCategoryKind Categorize(string component) =>
        _map.TryGetValue(component, out var c) ? c : RegressionCategoryKind.Unclassified;

    public bool IsClassified(string component) => _map.ContainsKey(component);

    private void Load(string path)
    {
        if (!File.Exists(path))
        {
            _logger.Warn("Ado", $"ComponentCategorizer: '{path}' not found — components stay Unclassified.");
            return;
        }
        try
        {
            var doc = XDocument.Load(path);
            foreach (var el in doc.Descendants("component"))
            {
                var name = (string?)el.Attribute("name");
                var category = (string?)el.Attribute("category");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(category)) continue;
                if (Enum.TryParse<RegressionCategoryKind>(category, ignoreCase: true, out var kind))
                    _map[name] = kind;
            }
            _logger.Info("Ado", $"ComponentCategorizer loaded {_map.Count} component categories.");
        }
        catch (Exception ex)
        {
            _logger.Error("Ado", $"ComponentCategorizer: failed to parse '{path}'.", ex);
        }
    }

    private static string FindCategoriesPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "impact", "component-categories.xml");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "docs", "impact", "component-categories.xml");
    }
}
