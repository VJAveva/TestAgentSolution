using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _initialLayoutComplete;

    public MainWindow()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = _vm;

        Loaded += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            _vm.SyncRegisteredAgents();

            // After initial layout, the TemplateTreeView may have auto-selected its root
            // which overwrites ActiveEditNode. Reset to WatchList root.
            _vm.EnsureWatchListSelected();
            _initialLayoutComplete = true;
        }, System.Windows.Threading.DispatcherPriority.Background);

        WatchListTreeView.SelectedItemChanged += (s, e) =>
        {
            if (e.NewValue is TreeNodeViewModel node) _vm.SelectedNode = node;
        };

        // Guard: don't let TemplateTree overwrite ActiveEditNode during initial render
        TemplateTreeView.SelectedItemChanged += (s, e) =>
        {
            if (e.NewValue is TreeNodeViewModel node)
            {
                _vm.SelectedTemplateNode = node;
                // Only set as active edit node if user has deliberately interacted
                if (_initialLayoutComplete)
                    _vm.ActiveEditNode = node;
            }
        };

        WatchListTreeView.PreviewMouseRightButtonDown += OnWatchListRightClick;
        TemplateTreeView.PreviewMouseRightButtonDown += OnTemplateRightClick;

        if (_vm.LogEntries is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
                    Dispatcher.InvokeAsync(() =>
                        LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]));
            };
        }
    }

    private void OnWatchListRightClick(object sender, MouseButtonEventArgs e)
    {
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (tvi is null) return;
        tvi.IsSelected = true;
        e.Handled = true;
        var node = tvi.DataContext as TreeNodeViewModel;
        if (node is null) return;

        var menu = MakeMenu();

        switch (node.NodeKind)
        {
            case "WatchList":
                Add(menu, "+ WatchItem", () => _vm.AddWatchItemCommand.Execute(null));
                break;

            case "WatchItem":
                // F3: Trigger all events
                AddGreen(menu, "\u25B6  Trigger All Events", () => _vm.TriggerWatchItemCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "Edit XML...", () => _vm.ToggleXmlEditorCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "Move Up", () => _vm.MoveUpCommand.Execute(null));
                Add(menu, "Move Down", () => _vm.MoveDownCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "+ Event", () => _vm.AddChildNodeCommand.Execute(null));
                AddDanger(menu, "Delete", () => _vm.DeleteSelectedNodeCommand.Execute(null));
                break;

            case "Event":
                // F3: Trigger this event
                AddGreen(menu, "\u25B6  Trigger Event", () => _vm.TriggerEventCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "Move Up", () => _vm.MoveUpCommand.Execute(null));
                Add(menu, "Move Down", () => _vm.MoveDownCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "+ ActionGroup", () => _vm.AddChildNodeCommand.Execute(null));
                Add(menu, "+ Action", () => _vm.AddActionToGroupCommand.Execute(null));
                Add(menu, "+ Ref", () => _vm.AddRefToGroupCommand.Execute(null));
                AddDanger(menu, "Delete", () => _vm.DeleteSelectedNodeCommand.Execute(null));
                break;

            default:
                Add(menu, "Move Up", () => _vm.MoveUpCommand.Execute(null));
                Add(menu, "Move Down", () => _vm.MoveDownCommand.Execute(null));
                menu.Items.Add(new Separator());
                Add(menu, "+ Child", () => _vm.AddChildNodeCommand.Execute(null));
                Add(menu, "+ Action", () => _vm.AddActionToGroupCommand.Execute(null));
                Add(menu, "+ Ref", () => _vm.AddRefToGroupCommand.Execute(null));
                menu.Items.Add(new Separator());
                AddDanger(menu, "Delete", () => _vm.DeleteSelectedNodeCommand.Execute(null));
                break;
        }

        tvi.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnTemplateRightClick(object sender, MouseButtonEventArgs e)
    {
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (tvi is null) return;
        tvi.IsSelected = true;
        e.Handled = true;
        var node = tvi.DataContext as TreeNodeViewModel;
        if (node is null) return;

        var menu = MakeMenu();

        if (node.NodeKind == "TemplateList")
        {
            Add(menu, "+ Template", () => _vm.AddTemplateCommand.Execute(null));
        }
        else
        {
            Add(menu, "Move Up", () => _vm.MoveTemplateUpCommand.Execute(null));
            Add(menu, "Move Down", () => _vm.MoveTemplateDownCommand.Execute(null));
            menu.Items.Add(new Separator());
            Add(menu, "+ Group", () => _vm.AddGroupToTemplateCommand.Execute(null));
            Add(menu, "+ Action", () => _vm.AddActionToTemplateCommand.Execute(null));
            Add(menu, "+ Ref", () => _vm.AddRefToTemplateCommand.Execute(null));
            Add(menu, "+ Init", () => _vm.AddInitializeToTemplateCommand.Execute(null));
            menu.Items.Add(new Separator());
            AddDanger(menu, "Delete", () => _vm.DeleteTemplateCommand.Execute(null));
        }

        tvi.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ContextMenu MakeMenu() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x58, 0x5B, 0x70)),
    };

    private static void Add(ContextMenu m, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        m.Items.Add(item);
    }

    private static void AddGreen(ContextMenu m, string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1))
        };
        item.Click += (_, _) => action();
        m.Items.Add(item);
    }

    private static void AddDanger(ContextMenu m, string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8))
        };
        item.Click += (_, _) => action();
        m.Items.Add(item);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
