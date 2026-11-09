using TestControllerGrpc.Editing;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

// Edit rights (ContextMenu_Requirement.md P4): Pipeline_Edit per pipeline, Template_Edit for the Library,
// Pipeline_Author for whole-WatchList changes, and no edits while the pipeline runs.
public sealed partial class MainViewModel
{
    /// <summary>What editing <paramref name="node"/> changes; <paramref name="structural"/> means the node itself is added, moved or deleted.</summary>
    internal static EditTarget EditTargetFor(TreeNodeViewModel? node, bool structural = false)
    {
        if (node is null || node.NodeKind == NodeKinds.WatchList) return EditTarget.WholeWatchList;
        if (node.NodeKind is NodeKinds.TemplateList or NodeKinds.Template || node.OwningTemplateId is not null)
            return EditTarget.Template;
        if (structural && node.NodeKind == NodeKinds.WatchItem) return EditTarget.WholeWatchList;
        return FindWatchItemTag(node) is { } tag ? EditTarget.Pipeline(tag) : EditTarget.WholeWatchList;
    }

    public string? EditBlockedReasonFor(TreeNodeViewModel? node, bool structural = false) =>
        EditBlockedReason(EditTargetFor(node, structural));

    private string? EditBlockedReason(EditTarget target) =>
        EditAuthorization.BlockedReason(target, (p, r) => _capabilityChecker.Can(p, r), IsWatchItemRunning);

    private bool EnsureCanEdit(TreeNodeViewModel? node, bool structural = false) =>
        EnsureCanEdit(EditTargetFor(node, structural));

    private bool EnsureCanEdit(EditTarget target)
    {
        if (EditBlockedReason(target) is not { } reason) return true;
        AddLog($"Edit blocked: {reason}", LogSeverity.Warning);
        return false;
    }
}
