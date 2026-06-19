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
    private void ApplyAgentPaneLayout(bool pinned)
    {
        if (pinned)
        {
            ColAgentPanel.Width = _savedAgentColWidth;
            ColAgentPanel.MinWidth = _agentPanelMinPinned;
            ColAgentSplitter.Width = GridLength.Auto;
            ColNodeProperties.Width = _savedPropertiesColWidth;
        }
        else
        {
            if (ColAgentPanel.Width.IsStar)
                _savedAgentColWidth = ColAgentPanel.Width;
            if (ColNodeProperties.Width.IsStar)
                _savedPropertiesColWidth = ColNodeProperties.Width;

            ColAgentPanel.Width = GridLength.Auto;
            ColAgentPanel.MinWidth = _agentPaneCollapsedWidth;
            ColAgentSplitter.Width = new GridLength(0);
            ColNodeProperties.Width = new GridLength(1, GridUnitType.Star);

            // Move focus to properties panel so keyboard users aren't stranded
            Dispatcher.InvokeAsync(() =>
            {
                var propertiesPanel = MainContentGrid.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(c => Grid.GetColumn(c) == 2 && Grid.GetRow(c) == 0);
                propertiesPanel?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ApplyLogPaneLayout(bool pinned)
    {
        if (pinned)
        {
            RowLogPane.Height = _savedLogRowHeight;
            RowLogPane.MinHeight = _logPaneMinHeight;
        }
        else
        {
            if (RowLogPane.Height.IsStar)
                _savedLogRowHeight = RowLogPane.Height;

            RowLogPane.Height = GridLength.Auto;
            RowLogPane.MinHeight = _logPaneCollapsedHeight;
        }
    }

    private void ApplyTreePaneLayout(bool pinned)
    {
        if (pinned)
        {
            ColTreePanel.Width = _savedTreeColWidth;
            ColTreePanel.MinWidth = _treePanelMinWidth;
        }
        else
        {
            if (ColTreePanel.Width.IsStar)
                _savedTreeColWidth = ColTreePanel.Width;

            ColTreePanel.Width = GridLength.Auto;
            ColTreePanel.MinWidth = _agentPaneCollapsedWidth; // same 28px collapsed width

            // Move keyboard focus to properties panel so user isn't stranded
            Dispatcher.InvokeAsync(() =>
            {
                var propertiesPanel = MainContentGrid.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(c => Grid.GetColumn(c) == 2 && Grid.GetRow(c) == 0);
                propertiesPanel?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // ── Design tokens ──────────────────────────────────────────────────────
    private void LoadDesignTokens()
    {
        if (TryFindResource("TreePanelMinWidth") is double treePanelMin)
            _treePanelMinWidth = treePanelMin;
        if (TryFindResource("AgentPanelMinPinned") is double agentMin)
            _agentPanelMinPinned = agentMin;
        if (TryFindResource("AgentPaneCollapsedWidth") is double agentCollapsed)
            _agentPaneCollapsedWidth = agentCollapsed;
        if (TryFindResource("LogPaneMinHeight") is double logMin)
            _logPaneMinHeight = logMin;
        if (TryFindResource("LogPaneCollapsedHeight") is double logCollapsed)
            _logPaneCollapsedHeight = logCollapsed;
        if (TryFindResource("TreePanelMaxWidthPercent") is double treeMax)
            _treePanelMaxWidthPercent = treeMax;
        if (TryFindResource("AgentPanelMaxWidthPercent") is double agentMax)
            _agentPanelMaxWidthPercent = agentMax;
        if (TryFindResource("LogPaneMaxHeightPercent") is double logMax)
            _logPaneMaxHeightPercent = logMax;
    }

    // ── MaxWidth / MaxHeight enforcement on resize ─────────────────────────
    private void MainContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.ActualWidth < 1 || grid.ActualHeight < 1)
            return;

        // Enforce max proportions using MaxWidth on grid columns.
        // This avoids converting star→pixel which breaks proportional layout.
        double maxTreeWidth = grid.ActualWidth * _treePanelMaxWidthPercent;
        ColTreePanel.MaxWidth = _vm.IsTreePanePinned ? maxTreeWidth : double.PositiveInfinity;

        double maxAgentWidth = grid.ActualWidth * _agentPanelMaxWidthPercent;
        ColAgentPanel.MaxWidth = _vm.IsAgentPanePinned ? maxAgentWidth : double.PositiveInfinity;

        // Log pane: clamp by ensuring main content row doesn't shrink below 250
        if (_vm.IsLogPanePinned)
        {
            double maxLogHeight = grid.ActualHeight * _logPaneMaxHeightPercent;
            double mainRowMinHeight = grid.ActualHeight - maxLogHeight - 4;
            if (mainRowMinHeight > 250)
                MainContentGrid.RowDefinitions[0].MinHeight = mainRowMinHeight;
        }
    }

    private void SetupInlineFolding()
    {
        if (_inlineEditor is null) return;
        if (_inlineFoldingManager is not null)
        {
            FoldingManager.Uninstall(_inlineFoldingManager);
            _inlineFoldingManager = null;
        }
        _inlineFoldingManager = FoldingManager.Install(_inlineEditor.TextArea);
        _inlineFoldingStrategy = new XmlFoldingStrategy();
        UpdateInlineFolding();
    }

    private void UpdateInlineFolding()
    {
        if (_inlineFoldingManager is not null && _inlineFoldingStrategy is not null && _inlineEditor is not null)
        {
            try { _inlineFoldingStrategy.UpdateFoldings(_inlineFoldingManager, _inlineEditor.Document); }
            catch (Exception) { /* XML parse errors expected during mid-edit � folding will retry on next keystroke */ }
        }
    }
}
