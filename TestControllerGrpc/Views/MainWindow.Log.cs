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
    // ???????????????????????????????????????????????????????????????????
    // LOG AUTO-SCROLL (GAP 1+4 fix)
    // Now subscribes to LogBufferService.BatchFlushed instead of per-item
    // CollectionChanged. One scroll per batch (every ~100ms) vs per item.
    // ???????????????????????????????????????????????????????????????????

    private void SubscribeToLogAutoScroll()
    {
        if (_vm.LogBuffer is not null)
        {
            _vm.LogBuffer.BatchFlushed += OnLogBatchFlushed;
        }
    }

    private void OnLogBatchFlushed(int count)
    {
        if (!_vm.IsAutoScrollEnabled || count == 0)
            return;

        // Already on UI thread (DispatcherTimer fires on dispatcher)
        if (LogListBox.Items.Count > 0)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void OnScrollToLogEntry(LogEntryViewModel entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (LogListBox.Items.Contains(entry))
            {
                LogListBox.ScrollIntoView(entry);
                LogListBox.SelectedItem = entry;
            }
        });
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

        if (sb.Length == 0) return;

        try
        {
            Clipboard.SetText(sb.ToString());
            _vm.StatusMessage = $"Copied {selected.Count} selected log entries to clipboard";
        }
        catch (ExternalException)
        {
            _vm.StatusMessage = "Clipboard is in use by another application";
        }
    }

    // ???????????????????????????????????????????????????????????????
    // KEYBOARD SHORTCUT SUPPORT
    // ???????????????????????????????????????????????????????????????

    private void OnFocusLogSearchRequested()
    {
        LogSearchBox.Focus();
        LogSearchBox.SelectAll();
    }

    // ???????????????????????????????????????????????????????????????
    // CLEANUP
    // ???????????????????????????????????????????????????????????????

    protected override void OnClosed(EventArgs e)
    {
        _vm.ScrollToLogEntry -= OnScrollToLogEntry;
        _vm.FocusLogSearchRequested -= OnFocusLogSearchRequested;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;

        if (_vm.LogBuffer is not null)
            _vm.LogBuffer.BatchFlushed -= OnLogBatchFlushed;

        WatchListTreeView.SelectedItemChanged -= OnWatchListSelectionChanged;
        WatchListTreeView.ContextMenuOpening -= OnWatchListContextMenuOpening;
        WatchListTreeView.PreviewMouseLeftButtonDown -= OnTreePreviewMouseDown;
        WatchListTreeView.PreviewMouseMove -= OnTreePreviewMouseMove;
        WatchListTreeView.DragOver -= OnTreeDragOver;
        WatchListTreeView.Drop -= OnTreeDrop;

        TemplateTreeView.SelectedItemChanged -= OnTemplateSelectionChanged;
        TemplateTreeView.ContextMenuOpening -= OnTemplateContextMenuOpening;

        Loaded -= OnWindowLoaded;

        if (_inlineFoldingManager is not null)
            FoldingManager.Uninstall(_inlineFoldingManager);

        _vm.Dispose();
        base.OnClosed(e);
    }
}
