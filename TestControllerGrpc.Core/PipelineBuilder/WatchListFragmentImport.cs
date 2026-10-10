using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.PipelineBuilder;

public enum ImportConflictKind
{
    DuplicateTag,
    TriggerFilterCollision,
    TemplateContentDiffers,
}

public sealed record ImportConflict(ImportConflictKind Kind, string Subject, string Message);

public sealed record ImportPlan(
    IReadOnlyList<WatchItemConfig> NewItems,
    IReadOnlyList<TemplateConfig> NewTemplates,
    IReadOnlyList<string> ReusableTemplates,
    IReadOnlyList<ImportConflict> Conflicts)
{
    public bool CanImport => Conflicts.Count == 0;
}

/// <summary>
/// Works out what importing a WatchList fragment into a live config would do, without doing it.
/// </summary>
/// <remarks>
/// The dangerous case is a template whose ID matches a live one but whose CONTENT differs. Silently
/// reusing the live one would repoint every pipeline already sharing it - on the live fleet that is
/// three pipelines per Sanity template - so it is reported as a conflict and never auto-resolved.
/// Builder-generated fragments carry no templates at all, so this only fires for hand-made ones.
/// </remarks>
public static class WatchListFragmentImport
{
    public static ImportPlan Plan(
        WatchListConfig live,
        IReadOnlyList<WatchItemConfig> incomingItems,
        IReadOnlyList<TemplateConfig> incomingTemplates)
    {
        var conflicts = new List<ImportConflict>();
        var newItems = new List<WatchItemConfig>();
        var newTemplates = new List<TemplateConfig>();
        var reusable = new List<string>();

        foreach (var item in incomingItems)
        {
            if (live.WatchItems.Any(w => string.Equals(w.Tag, item.Tag, StringComparison.OrdinalIgnoreCase)))
            {
                conflicts.Add(new ImportConflict(ImportConflictKind.DuplicateTag, item.Tag,
                    $"A pipeline tagged '{item.Tag}' already exists. Tags are identity keys - "
                    + "assignments and audit history are keyed on them, so they cannot be reused."));
                continue;
            }

            var clash = live.WatchItems.FirstOrDefault(w =>
                SamePath(w.Path, item.Path)
                && string.Equals(w.Filter, item.Filter, StringComparison.OrdinalIgnoreCase));

            if (clash is not null)
            {
                conflicts.Add(new ImportConflict(ImportConflictKind.TriggerFilterCollision, item.Tag,
                    $"'{clash.Tag}' already watches '{item.Path}' for '{item.Filter}'. "
                    + "Two pipelines on one trigger file would both fire."));
                continue;
            }

            newItems.Add(item);
        }

        foreach (var template in incomingTemplates)
        {
            var existing = live.Templates
                .FirstOrDefault(t => string.Equals(t.ID, template.ID, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                newTemplates.Add(template);
                continue;
            }

            if (SameContent(existing, template))
            {
                reusable.Add(template.ID);
                continue;
            }

            conflicts.Add(new ImportConflict(ImportConflictKind.TemplateContentDiffers, template.ID,
                $"Template '{template.ID}' already exists with different content. Reusing it would "
                + "change every pipeline that references it; rename the incoming one to import it."));
        }

        return new ImportPlan(newItems, newTemplates, reusable, conflicts);
    }

    /// <summary>
    /// Structural comparison through the serializer. NodeId is generated per construction and is not
    /// written to XML, so two structurally identical templates compare equal.
    /// </summary>
    private static bool SameContent(TemplateConfig left, TemplateConfig right) =>
        string.Equals(
            WatchListXmlParser.SerializeTemplatesToXml([left]),
            WatchListXmlParser.SerializeTemplatesToXml([right]),
            StringComparison.Ordinal);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        return string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

        static string Normalise(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (ArgumentException) { return path.TrimEnd('\\', '/'); }
        }
    }
}
