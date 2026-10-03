namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Pipeline each Templates-library template borrows settings from, for this session only.
/// </summary>
/// <remarks>
/// A template has no settings of its own - its tokens come from whichever pipeline Refs it. Without
/// a context, every Templates-library preview shows raw <c>[_Tokens]</c> and every run has to ask
/// which pipeline to borrow. Picking one here answers both, for the WHOLE template subtree at once,
/// because <see cref="TreeNodeViewModel.TokenScope"/> walks up to the owning Template node.
///
/// Deliberately NOT persisted: it is a browsing aid, not configuration. Saving it would be a second
/// place that decides which pipeline supplies a template's values, and the WatchList is the first.
/// It is cleared whenever the WatchList reloads, since the pipelines it names may no longer exist.
/// </remarks>
/// <summary>How a template's pipeline context was decided. Drives the chip suffix.</summary>
public enum TemplateContextSource
{
    /// <summary>Chosen explicitly by the user.</summary>
    Manual,

    /// <summary>A pipeline that Refs this template is running right now.</summary>
    Running,

    /// <summary>Exactly one pipeline Refs this template, so there was nothing to ask.</summary>
    Auto,
}

public static class TemplateRunContext
{
    private static readonly Dictionary<string, string> Contexts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TemplateContextSource> Sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Template ID whose context changed, or null when every context was cleared.</summary>
    public static event Action<string?>? Changed;

    /// <summary>Pipeline tag chosen for this template, or null when none has been chosen.</summary>
    public static string? For(string? templateId) =>
        !string.IsNullOrWhiteSpace(templateId) && Contexts.TryGetValue(templateId, out var tag) ? tag : null;

    /// <summary>How the current context was decided. Manual when nothing is set.</summary>
    public static TemplateContextSource SourceFor(string? templateId) =>
        !string.IsNullOrWhiteSpace(templateId) && Sources.TryGetValue(templateId, out var s)
            ? s : TemplateContextSource.Manual;

    public static void Set(string? templateId, string? pipelineTag) =>
        Set(templateId, pipelineTag, TemplateContextSource.Manual);

    public static void Set(string? templateId, string? pipelineTag, TemplateContextSource source)
    {
        if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(pipelineTag)) return;

        var sameTag = Contexts.TryGetValue(templateId, out var existing) && existing == pipelineTag;
        var sameSource = Sources.TryGetValue(templateId, out var prev) && prev == source;
        if (sameTag && sameSource) return;

        Contexts[templateId] = pipelineTag;
        Sources[templateId] = source;
        Changed?.Invoke(templateId);
    }

    public static void Clear(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return;
        Sources.Remove(templateId);
        if (Contexts.Remove(templateId)) Changed?.Invoke(templateId);
    }

    /// <summary>Called when the WatchList reloads and every node object is replaced.</summary>
    public static void ClearAll()
    {
        if (Contexts.Count == 0) return;
        Contexts.Clear();
        Sources.Clear();
        Changed?.Invoke(null);
    }
}
