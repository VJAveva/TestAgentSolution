using TestControllerGrpc.Authorization;

namespace TestControllerGrpc.Editing;

/// <summary>What an edit changes: one pipeline, a Library template, or the WatchList as a whole.</summary>
public sealed record EditTarget(string? PipelineTag, bool IsTemplate = false, bool IsWholeWatchList = false)
{
    public static EditTarget Pipeline(string tag) => new(tag);
    public static readonly EditTarget Template = new(null, IsTemplate: true);
    public static readonly EditTarget WholeWatchList = new(null, IsWholeWatchList: true);
}

/// <summary>
/// ContextMenu_Requirement.md rules 4 and 5. One rule set for every host: WPF asks it before each edit and
/// to disable menu items; REST edit endpoints ask it with the server-side authorization result.
/// </summary>
public static class EditAuthorization
{
    public const string PipelineRunning = "Pipeline is running";
    public const string TemplateAdminOnly = "Only administrators can edit templates - a change affects every pipeline that uses it.";
    public const string WholeWatchListAdminOnly = "Only administrators can create pipelines or change the whole WatchList.";

    /// <summary>Why the edit is not allowed now, or null when it is.</summary>
    public static string? BlockedReason(EditTarget target, Func<Permission, string?, bool> can, Func<string, bool> isRunning)
    {
        if (target.IsWholeWatchList)
            return can(Permission.Pipeline_Author, null) ? null : WholeWatchListAdminOnly;

        if (target.IsTemplate)
            return can(Permission.Template_Edit, null) ? null : TemplateAdminOnly;

        if (string.IsNullOrEmpty(target.PipelineTag)) return null;
        if (isRunning(target.PipelineTag)) return PipelineRunning;
        return can(Permission.Pipeline_Edit, target.PipelineTag)
            ? null
            : $"You do not have edit rights on '{target.PipelineTag}'.";
    }
}
