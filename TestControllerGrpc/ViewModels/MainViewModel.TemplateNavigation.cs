using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using TestControllerGrpc.Editing;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

// ContextMenu_Requirement.md 2.8.1 (P6): Edit template opens the Library at the matching node, shows who uses the
// template, and offers a way back. Navigation never touches the model, so unsaved pipeline edits are kept.
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _templateBannerText = "";
    [ObservableProperty] private bool _isTemplateBannerVisible;

    private TreeNodeViewModel? _navigationOrigin;

    /// <summary>The view switches to the Library tab and selects this node.</summary>
    public event Action<TreeNodeViewModel>? ShowInLibraryRequested;

    /// <summary>The view switches back to the WatchList tab and selects this node.</summary>
    public event Action<TreeNodeViewModel>? ShowInWatchListRequested;

    public void EditTemplate(TreeNodeViewModel node)
    {
        if (node.CalledTemplateId is not { } id) return;
        if (FindTemplateNode(id) is not { } templateNode)
        {
            HandleMissingTemplate(node, id);
            return;
        }

        var target = TreeNodeViewModel.MatchInTemplate(node, templateNode);
        for (var n = target.Parent; n is not null; n = n.Parent) n.IsExpanded = true;
        target.IsExpanded = true;

        var users = TemplateUsage.PipelinesReferencing(_config, id);
        var viewOnly = EditBlockedReason(EditTarget.Template) is not null ? " You can view but not edit templates." : "";
        TemplateBannerText = $"Editing template '{id}' - used by {users.Count} pipeline{(users.Count == 1 ? "" : "s")}: "
                             + $"{string.Join(", ", users)}. Changes apply to all of them.{viewOnly}";
        IsTemplateBannerVisible = true;
        _navigationOrigin = node;
        ShowInLibraryRequested?.Invoke(target);
    }

    [RelayCommand]
    private void BackToPipeline()
    {
        IsTemplateBannerVisible = false;
        var origin = _navigationOrigin;
        _navigationOrigin = null;
        if (origin is null) return;
        if (WatchListRoot is null || !ReferenceEquals(TreeNodeViewModel.RootOf(origin), WatchListRoot))
        {
            AddLog("The pipeline node you came from no longer exists (the tree was rebuilt).", LogSeverity.Warning);
            return;
        }
        ShowInWatchListRequested?.Invoke(origin);
    }

    [RelayCommand]
    private void CloseTemplateBanner()
    {
        IsTemplateBannerVisible = false;
        _navigationOrigin = null;
    }

    /// <summary>Enter on a template call: Edit template (2.8, default item).</summary>
    [RelayCommand]
    private void EditTemplateActive()
    {
        if (ActiveEditNode is { OwningTemplateCall: not null } node) EditTemplate(node);
    }

    private void HandleMissingTemplate(TreeNodeViewModel node, string id)
    {
        var answer = ThemedMessageBox.Show(
            $"Template '{id}' not found in Library.{Environment.NewLine}{Environment.NewLine}"
            + $"Yes - create an empty template '{id}' (administrators only){Environment.NewLine}"
            + $"No - delete this template call{Environment.NewLine}Cancel - do nothing",
            "Template not found", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.No)
        {
            SelectedNode = node;
            ConfirmDeleteTemplateCallCommand.Execute(null);
            return;
        }
        if (answer != MessageBoxResult.Yes || !EnsureCanEdit(EditTarget.Template)) return;

        using (BeginEdit($"Create template {id}"))
            _config.Templates.Add(new TemplateConfig { ID = id });
        _executor.LoadTemplates(_config.Templates);
        RebuildAllTrees();
        AddLog($"Created empty template '{id}'. Add its steps in the Library.");
    }
}
