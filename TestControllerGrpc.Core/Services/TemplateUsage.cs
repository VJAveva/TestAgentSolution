using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Which pipelines run a given Template.
///
/// <para>
/// A Template is a named list of action nodes with no parameters of its own - it carries no
/// Initialize, so nothing in it can resolve a [Token] until a WatchItem supplies one. Running a
/// template straight from the library therefore always fails on unresolved tokens. The fix is to
/// ask which pipeline to borrow settings from, and the candidates are exactly the pipelines that
/// <c>Ref</c> it.
/// </para>
/// </summary>
public static class TemplateUsage
{
    /// <summary>
    /// Tags of every WatchItem that reaches <paramref name="templateId"/> through a Ref, at any
    /// depth and through intermediate templates, in declaration order and without duplicates.
    /// </summary>
    /// <remarks>
    /// An empty result means the template is orphaned: no pipeline can supply its tokens, so there
    /// is nothing to run it as. Callers should disable the run affordance rather than let it fail.
    /// </remarks>
    public static IReadOnlyList<string> PipelinesReferencing(WatchListConfig config, string? templateId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(templateId)) return [];

        // Last definition wins, mirroring Dictionary assignment; duplicate IDs are a validator error
        // and must not throw here.
        var templates = new Dictionary<string, TemplateConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in config.Templates)
            if (!string.IsNullOrWhiteSpace(t.ID)) templates[t.ID] = t;

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wi in config.WatchItems)
        {
            if (string.IsNullOrWhiteSpace(wi.Tag)) continue;

            // Fresh per WatchItem: the visited set is a cycle guard, not a global memo, or the
            // second pipeline to reference a shared template would skip it.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reaches = wi.Events.Any(ev => Reaches(ev.Children, templateId!, templates, visited));

            if (reaches && seen.Add(wi.Tag)) tags.Add(wi.Tag);
        }

        return tags;
    }

    private static bool Reaches(
        List<IActionNode> nodes, string targetId,
        Dictionary<string, TemplateConfig> templates, HashSet<string> visited)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case RefConfig r:
                    if (string.Equals(r.TemplateID, targetId, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // A template may Ref another template; without the guard a cycle would recurse forever.
                    if (!visited.Add(r.TemplateID)) continue;
                    if (templates.TryGetValue(r.TemplateID, out var nested)
                        && Reaches(nested.Children, targetId, templates, visited))
                        return true;
                    break;

                case ActionGroupConfig g:
                    if (Reaches(g.Children, targetId, templates, visited)) return true;
                    break;
            }
        }
        return false;
    }
}
