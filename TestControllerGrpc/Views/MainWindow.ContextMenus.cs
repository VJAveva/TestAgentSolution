using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    // The items come from NodeMenuProvider (Core); this file only describes the node and renders the result.
    // Menus are rebuilt on every open because several items reflect the current state of THIS node.

    private void OnWatchListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = WatchListTreeView.SelectedItem as TreeNodeViewModel;
        // Activate before building: the menu and the Execute commands must target the same node.
        _vm.ActivateWatchListNode(node);
        WatchListTreeView.ContextMenu = RenderMenu(node, NodeMenuTree.WatchList);
    }

    private void OnTemplateContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = TemplateTreeView.SelectedItem as TreeNodeViewModel;
        _vm.ActivateTemplateNode(node);
        TemplateTreeView.ContextMenu = RenderMenu(node, NodeMenuTree.Library);
    }

    private ContextMenu RenderMenu(TreeNodeViewModel? node, NodeMenuTree tree)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "BgCard");
        menu.SetResourceReference(Control.ForegroundProperty, "TextP");
        menu.SetResourceReference(Control.BorderBrushProperty, "Bdr");
        if (node is null) return menu;

        NodeMenuGroup? group = null;
        foreach (var item in NodeMenuProvider.Build(MenuContextFor(node, tree)))
        {
            if (group is not null && item.Group != group) menu.Items.Add(new Separator());
            menu.Items.Add(RenderItem(item, node));
            group = item.Group;
        }
        return menu;
    }

    private NodeMenuContext MenuContextFor(TreeNodeViewModel node, NodeMenuTree tree)
    {
        var call = tree == NodeMenuTree.WatchList ? node.OwningTemplateCall : null;
        var skippable = node.ModelObject as ISkippableNode;
        var (retryCount, canRetry) = tree == NodeMenuTree.WatchList ? RetryInfo(node) : (0, false);
        var templateId = tree == NodeMenuTree.Library ? node.OwningTemplateId : null;

        return new NodeMenuContext
        {
            NodeKind = node.NodeKind,
            Tree = tree,
            InTemplateCall = call is not null,
            IsTemplateCallRoot = call is not null && ReferenceEquals(call, node),
            CalledTemplateId = node.CalledTemplateId,
            CalledTemplateExists = _vm.FindTemplateNode(node.CalledTemplateId) is not null,
            IsSkippable = skippable is not null,
            IsSkipped = skippable?.Skip == true,
            IsEnabled = node.IsEnabled,
            ExecutionType = node.ExecutionTypeText,
            CanMoveUp = MainViewModel.CanMoveNode(node, -1),
            CanMoveDown = MainViewModel.CanMoveNode(node, +1),
            RunBlockedReason = _vm.RunBlockedReasonFor(node),
            CanEnablePipelines = _capabilityChecker.Can(Permission.Pipeline_Enable),
            CanDisablePipelines = _capabilityChecker.Can(Permission.Pipeline_Disable),
            CanToggleThisPipeline = node.NodeKind == NodeKinds.WatchItem && _capabilityChecker.Can(
                node.IsEnabled ? Permission.Pipeline_Disable : Permission.Pipeline_Enable, node.Tag),
            CanAuthorPipelines = _capabilityChecker.Can(Permission.Pipeline_Author),
            PipelineBuilderEnabled = _vm.Features.PipelineBuilder,
            GlobalVariablesEnabled = _vm.Features.GlobalVariables,
            RetryFailedCount = retryCount,
            CanRetry = canRetry,
            OwningTemplateId = templateId,
            TemplateContext = templateId is null ? null : TemplateRunContext.For(templateId),
        };
    }

    private (int Count, bool CanRetry) RetryInfo(TreeNodeViewModel node)
    {
        var tag = node.NodeKind switch
        {
            NodeKinds.WatchItem => node.Tag,
            NodeKinds.Event => node.Parent?.Tag,
            _ => null,
        };
        if (string.IsNullOrEmpty(tag)) return (0, false);

        var last = App.Services.GetRequiredService<ExecutionSessionManager>().GetLastSession(tag);
        return (last?.FailedCount ?? 0, _vm.RetryFailedCommand.CanExecute(null));
    }

    private MenuItem RenderItem(NodeMenuItem item, TreeNodeViewModel node)
    {
        var mi = new MenuItem { Header = item.Label, IsEnabled = item.IsEnabled };
        AutomationProperties.SetName(mi, item.Label.TrimEnd('\u2026', '.'));
        if (item.ToolTip is not null)
        {
            mi.ToolTip = item.ToolTip;
            ToolTipService.SetShowOnDisabled(mi, true);
        }
        if (MenuIcons.TryGetValue(item.Id, out var icon))
        {
            var glyph = new TextBlock { Text = icon.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, icon.Brush);
            mi.Icon = glyph;
        }

        foreach (var child in item.Children) mi.Items.Add(RenderItem(child, node));
        if (item.Children.Count > 0) return mi;

        if (item.IsChecked is { } isChecked)
        {
            mi.IsCheckable = true;
            mi.IsChecked = isChecked;
        }

        var (command, click) = Bind(item.Id, node);
        if (command is not null) mi.Command = command;
        else if (click is not null) mi.Click += (_, _) => click(mi);
        return mi;
    }

    /// <summary>Maps every <see cref="NodeMenuIds"/> value to the command or handler that performs it.</summary>
    private (ICommand? Command, Action<MenuItem>? Click) Bind(string id, TreeNodeViewModel node) => id switch
    {
        NodeMenuIds.EnableAllWatchItems => (null, _ => _vm.SetPipelineEnabled(node, true)),
        NodeMenuIds.DisableAllWatchItems => (null, _ => _vm.SetPipelineEnabled(node, false)),
        NodeMenuIds.AddWatchItem => (_vm.AddWatchItemCommand, null),
        NodeMenuIds.NewPipeline => (null, _ => new Dialogs.PipelineBuilderWindow { Owner = this }.ShowDialog()),
        NodeMenuIds.ImportWatchItems => (_vm.ImportWatchItemsCommand, null),
        NodeMenuIds.ExportAllWatchItems => (_vm.ExportWatchItemsCommand, null),
        NodeMenuIds.EditWatchListXml => (_vm.OpenWatchListEditorCommand, null),
        NodeMenuIds.EditGlobalVariables => (_vm.EditGlobalVariablesCommand, null),

        NodeMenuIds.TriggerAllEvents => (_vm.TriggerWatchItemCommand, null),
        NodeMenuIds.CheckOnly => (_vm.CheckOnlyCommand, null),
        NodeMenuIds.RetryFailed => (_vm.RetryFailedCommand, null),
        NodeMenuIds.TriggerEvent => (_vm.TriggerEventCommand, null),
        NodeMenuIds.ExecuteGroup => (_vm.ExecuteGroupCommand, null),
        NodeMenuIds.ExecuteAction => (_vm.ExecuteSingleActionCommand, null),
        NodeMenuIds.AddEvent => (_vm.AddChildNodeCommand, null),
        NodeMenuIds.AddActionGroup => (_vm.AddActionGroupCommand, null),
        NodeMenuIds.AddAction => (_vm.AddActionToGroupCommand, null),
        NodeMenuIds.AddRef => (_vm.AddRefToGroupCommand, null),
        NodeMenuIds.AddInitialize => (_vm.AddInitializeToGroupCommand, null),
        NodeMenuIds.ExportWatchItem => (_vm.ExportWatchItemsCommand, null),
        NodeMenuIds.IncludeInTriggerAll => (null, mi => _vm.SetPipelineEnabled(node, mi.IsChecked)),
        NodeMenuIds.Delete => (_vm.ConfirmDeleteSelectedNodeCommand, null),

        NodeMenuIds.ExecutionType => (null, null),
        NodeMenuIds.ExecutionSequential => (null, _ => _vm.ChangeExecutionType(node, ExecutionMode.Sequential)),
        NodeMenuIds.ExecutionParallel => (null, _ => _vm.ChangeExecutionType(node, ExecutionMode.Parallel)),
        NodeMenuIds.Skip => (null, _ => AskSkipReason(node)),
        NodeMenuIds.Include => (null, _ => _vm.SetNodeSkip(node, false)),
        NodeMenuIds.MoveUp => (null, _ => _vm.MoveNodeUp(node)),
        NodeMenuIds.MoveDown => (null, _ => _vm.MoveNodeDown(node)),

        NodeMenuIds.EditTemplate => (null, _ => OpenTemplateInLibrary(_vm.FindTemplateNode(node.CalledTemplateId))),
        NodeMenuIds.DeleteTemplateCall => (_vm.ConfirmDeleteTemplateCallCommand, null),

        NodeMenuIds.AddTemplate => (_vm.AddTemplateCommand, null),
        NodeMenuIds.EditTemplatesXml => (_vm.EditTemplateXmlCommand, null),
        NodeMenuIds.ImportTemplates => (_vm.ImportTemplatesCommand, null),
        NodeMenuIds.ExportAllTemplates => (_vm.ExportTemplatesCommand, null),
        NodeMenuIds.ExecuteTemplate => (_vm.ExecuteTemplateCommand, null),
        NodeMenuIds.SetTemplateContext => (null, _ => _vm.ChangeTemplateContext(node)),
        NodeMenuIds.ClearTemplateContext => (null, _ => _vm.ClearTemplateContext(node)),
        NodeMenuIds.EditTemplateXml => (_vm.EditSingleTemplateXmlCommand, null),
        NodeMenuIds.AddGroupToTemplate => (_vm.AddGroupToTemplateCommand, null),
        NodeMenuIds.AddActionToTemplate => (_vm.AddActionToTemplateCommand, null),
        NodeMenuIds.AddRefToTemplate => (_vm.AddRefToTemplateCommand, null),
        NodeMenuIds.AddInitializeToTemplate => (_vm.AddInitializeToTemplateCommand, null),
        NodeMenuIds.ExportTemplate => (_vm.ExportTemplatesCommand, null),
        NodeMenuIds.DeleteFromLibrary => (_vm.DeleteTemplateCommand, null),

        _ => throw new InvalidOperationException($"Menu item '{id}' has no handler."),
    };

    private static readonly IReadOnlyDictionary<string, (string Glyph, string Brush)> MenuIcons =
        new Dictionary<string, (string, string)>
        {
            [NodeMenuIds.EnableAllWatchItems] = ("\uE73E", "AccGreen"),
            [NodeMenuIds.DisableAllWatchItems] = ("\uE711", "AccRed"),
            [NodeMenuIds.AddWatchItem] = ("\uE710", "Accent"),
            [NodeMenuIds.NewPipeline] = ("\uE710", "Accent"),
            [NodeMenuIds.ImportWatchItems] = ("\uE8B5", "Accent"),
            [NodeMenuIds.ExportAllWatchItems] = ("\uE898", "Accent"),
            [NodeMenuIds.EditWatchListXml] = ("\uE70F", "AccMauve"),
            [NodeMenuIds.EditGlobalVariables] = ("\uE8A1", "AccYellow"),
            [NodeMenuIds.TriggerAllEvents] = ("\uE768", "AccGreen"),
            [NodeMenuIds.CheckOnly] = ("\uE9D5", "AccBlue"),
            [NodeMenuIds.RetryFailed] = ("\uE72C", "AccPeach"),
            [NodeMenuIds.TriggerEvent] = ("\uEA80", "AccGreen"),
            [NodeMenuIds.ExecuteGroup] = ("\uE768", "AccGreen"),
            [NodeMenuIds.ExecuteAction] = ("\uE768", "AccGreen"),
            [NodeMenuIds.AddEvent] = ("\uEA80", "AccYellow"),
            [NodeMenuIds.AddActionGroup] = ("\uE8F1", "Accent"),
            [NodeMenuIds.AddAction] = ("\uE7C8", "AccPeach"),
            [NodeMenuIds.AddRef] = ("\uE71B", "AccMauve"),
            [NodeMenuIds.AddInitialize] = ("\uE713", "AccYellow"),
            [NodeMenuIds.ExportWatchItem] = ("\uE898", "Accent"),
            [NodeMenuIds.Delete] = ("\uE74D", "AccRed"),
            [NodeMenuIds.ExecutionType] = ("\uE8AB", "TextS"),
            [NodeMenuIds.MoveUp] = ("\uE74A", "TextS"),
            [NodeMenuIds.MoveDown] = ("\uE74B", "TextS"),
            [NodeMenuIds.EditTemplate] = ("\uE70F", "AccMauve"),
            [NodeMenuIds.DeleteTemplateCall] = ("\uE74D", "AccRed"),
            [NodeMenuIds.AddTemplate] = ("\uE710", "AccMauve"),
            [NodeMenuIds.EditTemplatesXml] = ("\uE70F", "AccMauve"),
            [NodeMenuIds.ImportTemplates] = ("\uE8B5", "AccMauve"),
            [NodeMenuIds.ExportAllTemplates] = ("\uE898", "AccMauve"),
            [NodeMenuIds.ExecuteTemplate] = ("\uE768", "AccGreen"),
            [NodeMenuIds.SetTemplateContext] = ("\uE8AB", "AccBlue"),
            [NodeMenuIds.ClearTemplateContext] = ("\uE711", "AccRed"),
            [NodeMenuIds.EditTemplateXml] = ("\uE70F", "AccMauve"),
            [NodeMenuIds.AddGroupToTemplate] = ("\uE8F1", "Accent"),
            [NodeMenuIds.AddActionToTemplate] = ("\uE7C8", "AccPeach"),
            [NodeMenuIds.AddRefToTemplate] = ("\uE71B", "AccMauve"),
            [NodeMenuIds.AddInitializeToTemplate] = ("\uE713", "AccYellow"),
            [NodeMenuIds.ExportTemplate] = ("\uE898", "AccMauve"),
            [NodeMenuIds.DeleteFromLibrary] = ("\uE74D", "AccRed"),
        };

    private void AskSkipReason(TreeNodeViewModel node)
    {
        if (node.ModelObject is not ISkippableNode skippable) return;
        var dialog = new Dialogs.SkipReasonDialog(node.DisplayText, skippable.SkipReason) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.SetNodeSkip(node, true, dialog.Reason);
    }

    /// <summary>Shows the template in the Library tab, selected and expanded.</summary>
    private void OpenTemplateInLibrary(TreeNodeViewModel? templateNode)
    {
        if (templateNode is null) return;
        LibraryTab.IsSelected = true;
        if (templateNode.Parent is { } root) root.IsExpanded = true;
        templateNode.IsExpanded = true;
        templateNode.IsSelected = true;
        _vm.ActivateTemplateNode(templateNode);
    }
}
