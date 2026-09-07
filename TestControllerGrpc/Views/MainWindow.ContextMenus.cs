using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.ViewModels;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    // ???????????????????????????????????????????????????????????????
    // FEATURE 2A: CONTEXT MENUS (built in code-behind)
    // ???????????????????????????????????????????????????????????????

    // ?? Context menu caches (rebuilt only when node kind changes) ???
    private string? _lastWatchListContextMenuNodeKind;
    private ContextMenu? _cachedWatchListContextMenu;
    private string? _lastTemplateContextMenuNodeKind;
    private ContextMenu? _cachedTemplateContextMenu;

    private void OnWatchListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = WatchListTreeView.SelectedItem as TreeNodeViewModel;
        var nodeKind = node?.NodeKind;
        // Always rebuild for WatchItem (IsEnabled toggle is node-specific)
        if (nodeKind != _lastWatchListContextMenuNodeKind || _cachedWatchListContextMenu is null
            || nodeKind is "WatchItem")
        {
            _cachedWatchListContextMenu = BuildWatchListContextMenu(node);
            _lastWatchListContextMenuNodeKind = nodeKind;
        }
        WatchListTreeView.ContextMenu = _cachedWatchListContextMenu;
    }

    private ContextMenu BuildWatchListContextMenu(TreeNodeViewModel? node)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("BgCard"),
            Foreground = (Brush)FindResource("TextP"),
            BorderBrush = (Brush)FindResource("Bdr"),
        };

        if (node is null) return menu;

        // ?? Execution commands ??????????????????????????????????????
        if (node.NodeKind is "WatchItem")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Trigger All Events", _vm.TriggerWatchItemCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());

            // Toggle: include/exclude this WatchItem from "Trigger All" execution
            var canToggleEnabled = _capabilityChecker.Can(
                node.IsEnabled ? Permission.Pipeline_Disable : Permission.Pipeline_Enable, node.Tag);
            var enabledItem = new MenuItem
            {
                Header = "Include in Trigger All",
                IsCheckable = true,
                IsChecked = node.IsEnabled,
                IsEnabled = canToggleEnabled,
                ToolTip = canToggleEnabled ? null : "Administrator only",
            };
            var capturedNode = node;
            enabledItem.Checked += (_, _) => capturedNode.IsEnabled = true;
            enabledItem.Unchecked += (_, _) => capturedNode.IsEnabled = false;
            menu.Items.Add(enabledItem);
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Event")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Trigger Event", _vm.TriggerEventCommand, "\uEA80", "AccGreen"));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Group", _vm.ExecuteGroupCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "Action")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Action", _vm.ExecuteSingleActionCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
        }
        if (node.NodeKind is "ActionGroup" or "Action")
        {
            var skippable = node.ModelObject as ISkippableNode;
            var skipItem = new MenuItem
            {
                Header = "Skip evaluator",
                IsCheckable = true,
                IsChecked = skippable?.Skip == true,
                ToolTip = "Skip this node during execution",
            };
            skipItem.Click += (_, _) => _vm.SetNodeSkip(node, skipItem.IsChecked);
            menu.Items.Add(skipItem);
            menu.Items.Add(new Separator());
        }

        // ?? Add commands (context-sensitive) ??????????????????????????
        if (node.NodeKind is "WatchList")
        {
            // Enable All / Disable All toggle for WatchList root
            var canEnable = _capabilityChecker.Can(Permission.Pipeline_Enable);
            var canDisable = _capabilityChecker.Can(Permission.Pipeline_Disable);

            var enableAllItem = CreateMenuItemWithIcon("Enable All WatchItems", null, "\uE73E", "AccGreen");
            enableAllItem.IsEnabled = canEnable;
            enableAllItem.ToolTip = canEnable ? null : "Administrator only";
            enableAllItem.Click += (_, _) => node.IsEnabled = true;
            menu.Items.Add(enableAllItem);

            var disableAllItem = CreateMenuItemWithIcon("Disable All WatchItems", null, "\uE711", "AccRed");
            disableAllItem.IsEnabled = canDisable;
            disableAllItem.ToolTip = canDisable ? null : "Administrator only";
            disableAllItem.Click += (_, _) => node.IsEnabled = false;
            menu.Items.Add(disableAllItem);
            menu.Items.Add(new Separator());

            menu.Items.Add(CreateMenuItemWithIcon("Add WatchItem", _vm.AddWatchItemCommand, "\uE710", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Import WatchItems…", _vm.ImportWatchItemsCommand, "\uE8B5", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Export All WatchItems…", _vm.ExportWatchItemsCommand, "\uE898", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Edit WatchList XML…", _vm.OpenWatchListEditorCommand, "\uE70F", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Edit Global Variables…", _vm.EditGlobalVariablesCommand, "\uE8A1", "AccYellow"));
        }
        else if (node.NodeKind is "WatchItem")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add Event", _vm.AddChildNodeCommand, "\uEA80", "AccYellow"));
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddActionGroupCommand, "\uE8F1", "Accent"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Export WatchItem�", _vm.ExportWatchItemsCommand, "\uE898", "Accent"));
        }
        else if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddActionGroupCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToGroupCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToGroupCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Initialize", _vm.AddInitializeToGroupCommand, "\uE713", "AccYellow"));
        }

        // ?? Change ExecutionType submenu (Event / ActionGroup) ??????
        if (node.NodeKind is "Event" or "ActionGroup")
        {
            menu.Items.Add(new Separator());
            var execTypeSubmenu = BuildExecutionTypeSubmenu(node);
            execTypeSubmenu.Icon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8AB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("TextS"),
            };
            menu.Items.Add(execTypeSubmenu);
        }

        // ?? Delete ??????????????????????????????????????????????????
        if (node.NodeKind is not "WatchList")
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.ConfirmDeleteSelectedNodeCommand, "\uE74D", "AccRed"));
        }

        // ?? Retry Failed (WatchItem/Event with previous failed session) ??
        if (node.NodeKind is "WatchItem" or "Event")
        {
            var retryTag = node.NodeKind == "WatchItem" ? node.Tag : node.Parent?.Tag;
            if (!string.IsNullOrEmpty(retryTag))
            {
                var sessionMgr = App.Services.GetRequiredService<ExecutionSessionManager>();
                var lastSession = sessionMgr.GetLastSession(retryTag);
                if (lastSession?.FailedCount > 0)
                {
                    menu.Items.Add(new Separator());
                    var retryItem = CreateMenuItemWithIcon(
                        $"Retry Failed ({lastSession.FailedCount} action{(lastSession.FailedCount > 1 ? "s" : "")})",
                        _vm.RetryFailedCommand, "\uE72C", "AccPeach");

                    // Phase 4: disable if user lacks Pipeline_Retry permission or lock conflict
                    if (!_vm.RetryFailedCommand.CanExecute(null))
                    {
                        retryItem.IsEnabled = false;
                        retryItem.ToolTip = "You do not have permission to retry this pipeline.";
                    }

                    menu.Items.Add(retryItem);
                }
            }
        }

        // ?? Move Up / Down ??????????????????????????????????????????
        if (CanShowMoveItems(node))
        {
            menu.Items.Add(new Separator());

            if (MainViewModel.CanMoveNode(node, -1))
            {
                var moveUp = CreateMenuItemWithIcon("Move Up", null!, "\uE74A", "TextS");
                moveUp.Command = null;
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = CreateMenuItemWithIcon("Move Down", null!, "\uE74B", "TextS");
                moveDown.Command = null;
                var capturedNode = node;
                moveDown.Click += (_, _) => _vm.MoveNodeDown(capturedNode);
                menu.Items.Add(moveDown);
            }
        }

        return menu;
    }

    private void OnTemplateContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = TemplateTreeView.SelectedItem as TreeNodeViewModel;
        var nodeKind = node?.NodeKind;
        if (nodeKind != _lastTemplateContextMenuNodeKind || _cachedTemplateContextMenu is null)
        {
            _cachedTemplateContextMenu = BuildTemplateContextMenu(node);
            _lastTemplateContextMenuNodeKind = nodeKind;
        }
        TemplateTreeView.ContextMenu = _cachedTemplateContextMenu;
    }

    private ContextMenu BuildTemplateContextMenu(TreeNodeViewModel? node)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("BgCard"),
            Foreground = (Brush)FindResource("TextP"),
            BorderBrush = (Brush)FindResource("Bdr"),
        };

        if (node is null) return menu;

        if (node.NodeKind is "TemplateList")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Add Template", _vm.AddTemplateCommand, "\uE710", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Edit Template XML", _vm.EditTemplateXmlCommand, "\uE70F", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Import Templates\u2026", _vm.ImportTemplatesCommand, "\uE8B5", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Export All Templates\u2026", _vm.ExportTemplatesCommand, "\uE898", "AccMauve"));
        }
        else if (node.NodeKind is "Template")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Template", _vm.ExecuteTemplateCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Edit Template XML…", _vm.EditSingleTemplateXmlCommand, "\uE70F", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddGroupToTemplateCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToTemplateCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToTemplateCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Initialize", _vm.AddInitializeToTemplateCommand, "\uE713", "AccYellow"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Export Template\u2026", _vm.ExportTemplatesCommand, "\uE898", "AccMauve"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }
        else if (node.NodeKind is "ActionGroup")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Group", _vm.ExecuteGroupCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
            AddSkipMenuItem(menu, node);
            menu.Items.Add(CreateMenuItemWithIcon("Add Action", _vm.AddActionToTemplateCommand, "\uE7C8", "AccPeach"));
            menu.Items.Add(CreateMenuItemWithIcon("Add ActionGroup", _vm.AddGroupToTemplateCommand, "\uE8F1", "Accent"));
            menu.Items.Add(CreateMenuItemWithIcon("Add Ref", _vm.AddRefToTemplateCommand, "\uE71B", "AccMauve"));
            menu.Items.Add(new Separator());
            var execTypeSubmenu = BuildExecutionTypeSubmenu(node);
            execTypeSubmenu.Icon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8AB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("TextS"),
            };
            menu.Items.Add(execTypeSubmenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }
        else if (node.NodeKind is "Action")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Execute Action", _vm.ExecuteSingleActionCommand, "\uE768", "AccGreen"));
            menu.Items.Add(new Separator());
            AddSkipMenuItem(menu, node);
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }
        else if (node.NodeKind is not "TemplateList")
        {
            menu.Items.Add(CreateMenuItemWithIcon("Delete", _vm.DeleteTemplateCommand, "\uE74D", "AccRed"));
        }

        // ?? Move Up / Down for templates ????????????????????????????
        if (CanShowMoveItems(node))
        {
            menu.Items.Add(new Separator());

            if (MainViewModel.CanMoveNode(node, -1))
            {
                var moveUp = CreateMenuItemWithIcon("Move Up", null!, "\uE74A", "TextS");
                moveUp.Command = null;
                var capturedNode = node;
                moveUp.Click += (_, _) => _vm.MoveNodeUp(capturedNode);
                menu.Items.Add(moveUp);
            }

            if (MainViewModel.CanMoveNode(node, +1))
            {
                var moveDown = CreateMenuItemWithIcon("Move Down", null!, "\uE74B", "TextS");
                moveDown.Command = null;
                var capturedNode = node;
                moveDown.Click += (_, _) => _vm.MoveNodeDown(capturedNode);
                menu.Items.Add(moveDown);
            }
        }

        return menu;
    }

    private void AddSkipMenuItem(ContextMenu menu, TreeNodeViewModel node)
    {
        var skippable = node.ModelObject as ISkippableNode;
        var skipItem = new MenuItem
        {
            Header = "Skip evaluator",
            IsCheckable = true,
            IsChecked = skippable?.Skip == true,
            ToolTip = "Skip this node during execution",
        };
        skipItem.Click += (_, _) => _vm.SetNodeSkip(node, skipItem.IsChecked);
        menu.Items.Add(skipItem);
    }

    private static bool CanShowMoveItems(TreeNodeViewModel node)
    {
        // Initialize is always first � no move
        if (node.NodeKind is "WatchList" or "TemplateList" or "Initialize") return false;
        return MainViewModel.CanMoveNode(node, -1) || MainViewModel.CanMoveNode(node, +1);
    }

    private static MenuItem CreateMenuItem(string header, ICommand command)
    {
        return new MenuItem { Header = header, Command = command };
    }

    private MenuItem CreateMenuItemWithIcon(string header, ICommand command, string iconGlyph, string resourceColorKey)
    {
        var item = new MenuItem { Header = header, Command = command };
        item.Icon = new System.Windows.Controls.TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (Brush)FindResource(resourceColorKey),
        };
        return item;
    }

    /// <summary>Builds a "Change Execution Type" submenu with Sequential/Parallel options.</summary>
    private MenuItem BuildExecutionTypeSubmenu(TreeNodeViewModel node)
    {
        var submenu = new MenuItem { Header = "Change Execution Type" };

        var isSequential = string.Equals(node.ExecutionTypeText, "Sequential", StringComparison.OrdinalIgnoreCase);
        var isParallel = string.Equals(node.ExecutionTypeText, "Parallel", StringComparison.OrdinalIgnoreCase);

        var seqItem = new MenuItem
        {
            Header = "Sequential",
            IsCheckable = true,
            IsChecked = isSequential
        };
        var capturedNode1 = node;
        seqItem.Click += (_, _) => _vm.ChangeExecutionType(capturedNode1, Models.ExecutionMode.Sequential);

        var parItem = new MenuItem
        {
            Header = "Parallel",
            IsCheckable = true,
            IsChecked = isParallel
        };
        var capturedNode2 = node;
        parItem.Click += (_, _) => _vm.ChangeExecutionType(capturedNode2, Models.ExecutionMode.Parallel);

        submenu.Items.Add(seqItem);
        submenu.Items.Add(parItem);

        return submenu;
    }
}
