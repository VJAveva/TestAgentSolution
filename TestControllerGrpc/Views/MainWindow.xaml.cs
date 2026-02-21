using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

        if (_vm.FilteredLogEntries is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, e) =>
            {
                if (_vm.IsAutoScrollEnabled && e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
                    Dispatcher.InvokeAsync(() =>
                        LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]));
            };
        }

        // Wire scroll-to-error: scroll log to specific entry on failure
        _vm.ScrollToLogEntry += entry =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (LogListBox.Items.Contains(entry))
                {
                    LogListBox.ScrollIntoView(entry);
                    LogListBox.SelectedItem = entry;
                }
            });
        };
    }

    /// <summary>Copies selected log entries to clipboard (from context menu).</summary>
    private void OnCopySelectedLog(object sender, RoutedEventArgs e)
    {
        var selected = LogListBox.SelectedItems;
        if (selected.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in selected)
        {
            if (item is LogEntryViewModel entry)
                sb.AppendLine(entry.FullText);
        }
        Clipboard.SetText(sb.ToString());
        _vm.StatusMessage = $"Copied {selected.Count} selected log entries to clipboard";
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
