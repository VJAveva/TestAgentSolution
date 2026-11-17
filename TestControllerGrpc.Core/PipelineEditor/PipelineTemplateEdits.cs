using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>A save-blocking problem a template change causes in a pipeline that uses the template.</summary>
public sealed record TemplateImpactProblem(string Pipeline, string Message);

/// <summary>Template operations of the Pipeline Editor (PipelineEditor_Requirement.md section 4), on editor text.</summary>
public static class PipelineTemplateEdits
{
    /// <summary>The document a template tab edits: one template inside <c>&lt;Templates&gt;</c>.</summary>
    public static string TemplateDocument(TemplateConfig template) => WatchListXmlParser.SerializeTemplateList([template]);

    /// <summary>
    /// Copy for this pipeline only: replaces the Ref at <paramref name="refPath"/> with the template's steps
    /// (and anything between them, such as XML comments), re-indented to the Ref's position.
    /// </summary>
    public static PipelineXmlEditResult? InlineTemplate(string xml, string refPath, string templateDocument)
    {
        if (PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(refPath) is not { Kind: NodeKinds.Ref } call)
            return null;
        if (PipelineXmlMap.TryBuild(templateDocument) is not { Roots: [{ Kind: NodeKinds.Template } template, ..] })
            return null;

        string text;
        if (template.Children.Count == 0)
        {
            var (start, end) = PipelineXmlText.LineBlock(xml, call.Start, call.End);
            text = xml.Remove(start, end - start);
        }
        else
        {
            var first = template.Children[0];
            var block = templateDocument[first.Start..template.Children[^1].End];
            var steps = Reindent(block, PipelineXmlText.IndentOf(templateDocument, first.Start),
                PipelineXmlText.IndentOf(xml, call.Start), PipelineXmlText.NewLineOf(xml));
            text = xml[..call.Start] + steps + xml[call.End..];
        }
        return new PipelineXmlEditResult(text, template.Children.Count == 0 ? PipelinePaths.Parent(refPath) ?? "" : refPath);
    }

    /// <summary>
    /// Save group as template: the whole group (its name and execution type included) becomes the template's
    /// only step, and a Ref to the new template takes its place in the pipeline.
    /// </summary>
    public static (PipelineXmlEditResult Pipeline, string TemplateDocument)? GroupToTemplate(
        string xml, string groupPath, string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)
            || PipelineXmlMap.TryBuild(xml) is not { } map
            || map.Find(groupPath) is not { Kind: NodeKinds.ActionGroup } group)
            return null;

        var call = $"<Ref TemplateID=\"{PipelineXmlText.Escape(templateId.Trim(), '"')}\" />";
        var groupText = Reindent(xml[group.Start..group.End], PipelineXmlText.IndentOf(xml, group.Start), "    ", "\n");
        var document = $"<Templates>\n  <Template ID=\"{PipelineXmlText.Escape(templateId.Trim(), '"')}\">\n    {groupText}\n  </Template>\n</Templates>";
        return (new PipelineXmlEditResult(xml[..group.Start] + call + xml[group.End..], groupPath), document);
    }

    /// <summary>
    /// Runs the checker on every pipeline that uses one of <paramref name="changed"/> templates, with the changes
    /// applied. Only errors the change introduces block: a pipeline that was already broken does not lock its templates.
    /// </summary>
    public static IReadOnlyList<TemplateImpactProblem> CheckPipelinesUsing(
        WatchListConfig context,
        IReadOnlyList<(string? OriginalId, TemplateConfig Template)> changed,
        WatchItemConfig? editedPipeline,
        IReadOnlyCollection<string>? knownAgents,
        IPreflightFileSystem? fileSystem)
    {
        if (changed.Count == 0) return [];
        var replaced = new HashSet<string>(
            changed.Select(c => c.OriginalId ?? c.Template.ID).Where(id => !string.IsNullOrEmpty(id))!,
            StringComparer.OrdinalIgnoreCase);
        var after = context.Templates.Where(t => !replaced.Contains(t.ID)).Concat(changed.Select(c => c.Template)).ToList();
        var items = context.WatchItems
            .Select(w => editedPipeline is not null && string.Equals(w.Tag, editedPipeline.Tag, StringComparison.OrdinalIgnoreCase)
                ? editedPipeline : w)
            .ToList();

        // Users before the change (by the old ID) and after it (by the new ID): a rename breaks the former.
        var users = changed
            .SelectMany(c => (c.OriginalId is null ? [] : TemplateUsage.PipelinesReferencing(
                    new WatchListConfig { Templates = context.Templates, WatchItems = items }, c.OriginalId))
                .Concat(TemplateUsage.PipelinesReferencing(new WatchListConfig { Templates = after, WatchItems = items }, c.Template.ID)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var problems = new List<TemplateImpactProblem>();
        foreach (var tag in users)
        {
            var item = items.First(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));
            var before = Errors(context.Templates, item).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(Errors(after, item).Where(e => !before.Contains(e)).Select(e => new TemplateImpactProblem(tag, e)));
        }
        return problems;

        IEnumerable<string> Errors(List<TemplateConfig> templates, WatchItemConfig item) =>
            WatchListValidator.Analyze(
                    new WatchListConfig { GlobalVariablesFile = context.GlobalVariablesFile, Templates = templates, WatchItems = [item] },
                    knownAgents, fileSystem ?? new PreflightFileSystem())
                .Where(i => i.Severity == WatchIssueSeverity.Error)
                .Select(i => i.Message);
    }

    /// <summary>Moves a multi-line element from one indent to another; the first line is placed by the caller.</summary>
    private static string Reindent(string block, string fromIndent, string toIndent, string newLine)
    {
        var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].StartsWith(fromIndent, StringComparison.Ordinal))
                lines[i] = toIndent + lines[i][fromIndent.Length..];
        return string.Join(newLine, lines);
    }
}
