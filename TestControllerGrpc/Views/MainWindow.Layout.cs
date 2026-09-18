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
using TestControllerGrpc.Views.Behaviors;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.Views;

public partial class MainWindow : Window
{
    private readonly IUiLayoutStore _layoutStore = new UiLayoutStore();

    /// <summary>What the USER asked for, which is not what is on screen once a narrow window forces
    /// panes shut. Persisting this rather than the live flags is what lets a resize be non-destructive.</summary>
    private (bool Tree, bool Agent, bool Log) _paneIntent = (true, true, true);

    private Breakpoint _band = Breakpoint.Wide;
    private bool _applyingBreakpoint;

    /// <summary>Only a real user toggle redefines intent; our own breakpoint writes must not.</summary>
    private void NotePaneIntent()
    {
        if (_applyingBreakpoint) return;
        _paneIntent = (_vm.IsTreePanePinned, _vm.IsAgentPanePinned, _vm.IsLogPanePinned);
    }

    // The agent pane yields first, then the tree; the log is left to the user's own collapse toggle.
    private static (bool Tree, bool Agent, bool Log) PanesFor(
        (bool Tree, bool Agent, bool Log) intent, Breakpoint band) => LayoutBreakpoint.PanesFor(intent, band);

    private void ApplyBreakpoint(double width)
    {
        var band = LayoutBreakpoint.Classify(width);
        if (band == _band) return;

        _band = band;
        SetPanes(PanesFor(_paneIntent, band));
    }

    private void SetPanes((bool Tree, bool Agent, bool Log) panes)
    {
        _applyingBreakpoint = true;
        try
        {
            _vm.IsTreePanePinned = panes.Tree;
            _vm.IsAgentPanePinned = panes.Agent;
            _vm.IsLogPanePinned = panes.Log;
        }
        finally
        {
            _applyingBreakpoint = false;
        }

        ApplyTreePaneLayout(panes.Tree);
        ApplyAgentPaneLayout(panes.Agent);
        ApplyLogPaneLayout(panes.Log);
    }

    /// <summary>
    /// Layouts are kept per display configuration - a layout tuned on a 4K desktop is wrong when the
    /// laptop is undocked, and restoring it blindly leaves panes off-screen or unusably narrow.
    /// </summary>
    private static string DisplayKey()
    {
        try
        {
            return string.Join("|", System.Windows.Forms.Screen.AllScreens
                .Select(s => $"{s.Bounds.Width}x{s.Bounds.Height}")
                .OrderBy(s => s, StringComparer.Ordinal));
        }
        catch (Exception)
        {
            return "default";
        }
    }

    private void RestoreLayout()
    {
        // Seed intent from the view model first: with no saved layout we still must persist what the
        // user actually started with, not this field's initialiser.
        _paneIntent = (_vm.IsTreePanePinned, _vm.IsAgentPanePinned, _vm.IsLogPanePinned);

        var saved = _layoutStore.Load(DisplayKey());
        if (saved is null) return;

        _savedTreeColWidth = new GridLength(saved.TreeWidth, GridUnitType.Star);
        _savedPropertiesColWidth = new GridLength(saved.PropertiesWidth, GridUnitType.Star);
        _savedAgentColWidth = new GridLength(saved.AgentWidth, GridUnitType.Star);
        _savedLogRowHeight = new GridLength(saved.LogHeight, GridUnitType.Star);

        _vm.IsLogCollapsed = saved.LogCollapsed;
        _paneIntent = (saved.TreePinned, saved.AgentPinned, saved.LogPinned);

        // Applied explicitly: if a flag already equalled the saved value no PropertyChanged fires,
        // so the restored sizes above would never reach the grid.
        SetPanes(PanesFor(_paneIntent, _band));
    }

    private void SaveLayout()
    {
        // A collapsed pane holds GridLength.Auto, whose Value is meaningless - fall back to the last
        // starred size, which is exactly what the pin/unpin code keeps in the _saved* fields.
        static double Star(GridLength live, GridLength fallback) => live.IsStar ? live.Value : fallback.Value;

        _layoutStore.Save(DisplayKey(), new UiLayoutSnapshot
        {
            TreeWidth = Star(ColTreePanel.Width, _savedTreeColWidth),
            PropertiesWidth = Star(ColNodeProperties.Width, _savedPropertiesColWidth),
            AgentWidth = Star(ColAgentPanel.Width, _savedAgentColWidth),
            LogHeight = Star(RowLogPane.Height, _savedLogRowHeight),
            TreePinned = _paneIntent.Tree,
            AgentPinned = _paneIntent.Agent,
            LogPinned = _paneIntent.Log,
            LogCollapsed = _vm.IsLogCollapsed,
        });
    }

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

        ApplyBreakpoint(grid.ActualWidth);

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
