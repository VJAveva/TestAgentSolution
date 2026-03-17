using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Inline XML Editor + Raw XML Editor + WatchList Editor ???????????
public sealed partial class MainViewModel
{
    // ???????????????????????????????????????????????????????????????
    // F1-INLINE: INLINE XML EDITOR (AvalonEdit panel in center)
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ToggleInlineXmlEditor()
    {
        if (IsInlineXmlEditorVisible)
        {
            // Close inline editor
            IsInlineXmlEditorVisible = false;
            InlineXmlEditorStatus = "";
            return;
        }

        WriteBackAll();

        if (SelectedNode?.NodeKind == "WatchItem" && SelectedNode.ModelObject is WatchItemConfig wi)
        {
            InlineXmlEditorScope = "WatchItem";
            InlineXmlEditorText = WatchListXmlParser.SerializeWatchItem(wi);
        }
        else
        {
            // Default: entire WatchList
            InlineXmlEditorScope = "WatchList";
            InlineXmlEditorText = WatchListXmlParser.SerializeWatchList(_config);
        }

        InlineXmlEditorStatus = "";
        IsInlineXmlEditorVisible = true;
    }

    [RelayCommand]
    private void ApplyInlineXml()
    {
        try
        {
            if (InlineXmlEditorScope == "WatchItem")
            {
                if (SelectedNode?.NodeKind != "WatchItem") return;
                var oldWi = SelectedNode.ModelObject as WatchItemConfig;
                if (oldWi is null) return;

                var parsed = WatchListXmlParser.DeserializeWatchItem(InlineXmlEditorText);
                if (parsed is null)
                {
                    InlineXmlEditorStatus = "Error: Root element must be <WatchItem>.";
                    return;
                }

                var idx = _config.WatchItems.IndexOf(oldWi);
                if (idx >= 0) _config.WatchItems[idx] = parsed;

                if (WatchListRoot is not null)
                {
                    var treeIdx = WatchListRoot.Children.IndexOf(SelectedNode);
                    if (treeIdx >= 0)
                    {
                        var newNode = TreeNodeViewModel.FromWatchItem(parsed);
                        newNode.Parent = WatchListRoot;
                        WatchListRoot.Children[treeIdx] = newNode;
                        SelectedNode = newNode;
                    }
                }
                WatchListRoot?.RefreshDisplayText();
                InlineXmlEditorStatus = "WatchItem applied successfully.";
                AddLog($"WatchItem updated via inline XML editor: {parsed.Tag}", LogSeverity.Success);
            }
            else
            {
                // Full WatchList
                var parsed = WatchListXmlParser.DeserializeWatchList(InlineXmlEditorText);
                if (parsed is null)
                {
                    InlineXmlEditorStatus = "Error: Root element must be <WatchList>.";
                    return;
                }

                parsed.FilePath = _config.FilePath;
                ApplyConfig(parsed);
                InlineXmlEditorStatus = $"Applied: {parsed.WatchItems.Count} WatchItems, {parsed.Templates.Count} Templates.";
                AddLog($"WatchList updated via inline XML editor", LogSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            InlineXmlEditorStatus = $"XML error: {ex.Message}";
            AddLog($"Inline XML editor error: {ex.Message}", LogSeverity.Error);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // F2: RAW XML EDITOR  (opens separate modal window)
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ToggleXmlEditor()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    [RelayCommand]
    private void ApplyXmlEditor()
    {
        // Kept for backward compatibility — delegates to the window flow
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    [RelayCommand]
    private void RevertXmlEditor()
    {
        // No-op: revert is now handled inside the editor window
    }

    [RelayCommand]
    private void EditWatchItemXml()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        OpenRawXmlEditorWindow();
    }

    /// <summary>Opens the standalone Raw XML Editor window for the selected WatchItem.</summary>
    private void OpenRawXmlEditorWindow()
    {
        if (SelectedNode?.NodeKind != "WatchItem") return;
        if (SelectedNode.ModelObject is not WatchItemConfig oldWi) return;

        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchItem(oldWi);

        var editorVm = new WatchItemXmlEditorViewModel(xml);
        editorVm.WindowTitle = $"WatchItem XML Editor — {oldWi.Tag}";

        var editorWindow = new Views.RawXmlEditorWindow(editorVm);

        // Set owner to main window for CenterOwner positioning
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var parsed = WatchListXmlParser.DeserializeWatchItem(editorVm.ResultXml);
                if (parsed is null)
                {
                    XmlEditorStatus = "Error: Root element must be <WatchItem>.";
                    AddLog("XML editor: Root element was not <WatchItem>", LogSeverity.Error);
                    return;
                }

                // Replace in model
                var idx = _config.WatchItems.IndexOf(oldWi);
                if (idx >= 0) _config.WatchItems[idx] = parsed;

                // Replace in tree
                if (WatchListRoot is not null)
                {
                    var treeIdx = WatchListRoot.Children.IndexOf(SelectedNode);
                    if (treeIdx >= 0)
                    {
                        var newNode = TreeNodeViewModel.FromWatchItem(parsed);
                        newNode.Parent = WatchListRoot;
                        WatchListRoot.Children[treeIdx] = newNode;
                        SelectedNode = newNode;
                    }
                }
                WatchListRoot?.RefreshDisplayText();
                XmlEditorStatus = "Applied successfully.";
                IsXmlEditorOpen = false;
                AddLog($"WatchItem updated via XML editor: {parsed.Tag}", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                XmlEditorStatus = $"XML error: {ex.Message}";
                AddLog($"XML editor apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    // ???????????????????????????????????????????????????????????????
    // FULL WATCHLIST XML EDITOR
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Opens the full WatchList XML in the standalone editor window.
    /// Apply re-parses and diff-merges the config without destroying running watchers.
    /// </summary>
    [RelayCommand]
    private void OpenWatchListEditor()
    {
        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchList(_config);

        var editorVm = new WatchItemXmlEditorViewModel(xml);
        editorVm.WindowTitle = "WatchList XML Editor — Full";

        var editorWindow = new Views.RawXmlEditorWindow(editorVm);
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var newConfig = WatchListXmlParser.DeserializeWatchList(editorVm.ResultXml);
                if (newConfig is null)
                {
                    AddLog("XML editor: Root element must be <WatchList>.", LogSeverity.Error);
                    return;
                }

                // Apply diff-based merge if executions are active
                if (_sessionManager.HasAnyActiveExecution)
                {
                    _watcherManager.ApplyDiff(newConfig.WatchItems);
                }
                else
                {
                    _watcherManager.ApplyConfig(newConfig);
                }

                _config.WatchItems.Clear();
                _config.WatchItems.AddRange(newConfig.WatchItems);
                _config.Templates.Clear();
                _config.Templates.AddRange(newConfig.Templates);
                _config.FilePath = VocabFilePath;

                _executor.LoadTemplates(_config.Templates);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        TreeRoots.Clear();
                        TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
                        TemplateRoots.Clear();
                        TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                        RebuildTemplateIds();
                        RebuildFilterOptions();
                        LoadTokensFromConfig(_config);
                        ActiveWatchers = _watcherManager.ActiveWatcherCount;
                        StatusMessage = $"{_config.WatchItems.Count} WatchItems, {_config.Templates.Count} Templates";
                    }
                    catch (Exception ex)
                    {
                        _appLogger.Error("UI", "Failed to rebuild tree from XML editor", ex);
                    }
                });

                AddLog("? WatchList updated from XML editor.", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                AddLog($"? WatchList XML apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }
}
