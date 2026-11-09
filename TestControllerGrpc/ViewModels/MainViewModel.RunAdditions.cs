using System.Windows;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

// ContextMenu_Requirement.md P7: Run from here to end, Show resolved command, Open last output, Stop.
public sealed partial class MainViewModel
{
    /// <summary>One-shot skip set for the next TriggerEvent; taken (and cleared) at its very start.</summary>
    private (IReadOnlySet<IActionNode> Skips, string Path)? _pendingPartialRun;

    public string? PipelineTagOf(TreeNodeViewModel? node) => FindWatchItemTag(node);

    public bool IsPipelineRunning(string? tag) => tag is not null && IsWatchItemRunning(tag);

    private (IReadOnlySet<IActionNode> Skips, string Path)? TakePendingPartialRun()
    {
        var pending = _pendingPartialRun;
        _pendingPartialRun = null;
        return pending;
    }

    /// <summary>Runs the node's Event from this node to the end; earlier steps are skipped for this run only.</summary>
    public void RunFromHere(TreeNodeViewModel node)
    {
        if (node.IsRefExpansion || node.ModelObject is not IActionNode start) return;
        var eventNode = node.Parent;
        while (eventNode is not null && eventNode.NodeKind != NodeKinds.Event) eventNode = eventNode.Parent;
        if (eventNode?.ModelObject is not EventConfig evt) return;

        if (PartialRun.StepsBefore(evt, start) is not { } skips)
        {
            AddLog($"Run from here: '{node.DisplayText}' is not a step of its event.", LogSeverity.Warning);
            return;
        }

        var tag = FindWatchItemTag(node) ?? "";
        var path = string.Join(" > ", PathOf(node));
        if (ThemedMessageBox.Show(
                $"Run '{node.Tag}' to the end of event '{evt.Type}' on pipeline '{tag}'?{Environment.NewLine}"
                + $"{skips.Count} step(s) before it will NOT run.",
                "Run from here", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _pendingPartialRun = (skips, path);
        SelectedNode = eventNode;
        if (!TriggerEventCommand.CanExecute(null))
        {
            _pendingPartialRun = null;
            AddLog($"Run from here: pipeline '{tag}' cannot run now.", LogSeverity.Warning);
            return;
        }
        TriggerEventCommand.Execute(null);
    }

    private static IEnumerable<string> PathOf(TreeNodeViewModel node)
    {
        var parts = new Stack<string>();
        for (var n = node; n is not null && n.NodeKind != NodeKinds.WatchList; n = n.Parent)
            parts.Push(string.IsNullOrWhiteSpace(n.Tag) ? n.NodeKind : n.Tag);
        return parts;
    }

    public void ShowResolvedCommand(TreeNodeViewModel node)
    {
        if (node.NodeKind != NodeKinds.Action) return;
        ThemedMessageBox.Show(
            RunInfoText.ResolvedCommand(node.ResolvedAgentName, node.ResolvedCommand, node.ResolvedParameters),
            $"Resolved command - {node.Tag}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void OpenLastOutput(TreeNodeViewModel node)
    {
        if (node.NodeKind != NodeKinds.Action || FindWatchItemTag(node) is not { } tag) return;
        var session = _sessionManager.GetLastSession(tag);
        var tags = new[] { node.Tag, node.ResolvedTag };
        var result = session?.ActionResults
            .Where(r => tags.Contains(r.ActionTag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Sequence)
            .FirstOrDefault();
        ThemedMessageBox.Show(RunInfoText.LastResult(result, node.Tag, session?.StartedUtc),
            $"Last output - {node.Tag}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Stops the running session of the node's pipeline, after the same per-pipeline check as Cancel.</summary>
    public async Task StopPipelineAsync(TreeNodeViewModel node)
    {
        if (FindWatchItemTag(node) is not { } tag) return;
        var session = ActiveSessions.FirstOrDefault(s =>
            s.Status == "Running" && string.Equals(s.WatchItemTag, tag, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        try
        {
            await _pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Cancel, tag);
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            AddLog($"Stop denied for '{tag}': {ex.Message}", LogSeverity.Warning);
            ShowAuthorizationDeniedDialog(ex.Message);
            return;
        }
        CancelSessionCommand.Execute(session);
        AddLog($"Stop requested for '{tag}'");
    }

    public bool CanStopPipeline(string? tag) => tag is not null && _capabilityChecker.Can(Permission.Pipeline_Cancel, tag);
}
