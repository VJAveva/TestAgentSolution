using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Raw XML Editor + WatchList Editor ???????????????????????????????
public sealed partial class MainViewModel
{
    // ???????????????????????????????????????????????????????????????
    // F2: RAW XML EDITOR  (opens separate modal window)
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void ToggleXmlEditor()
    {
        if (SelectedNode?.NodeKind != NodeKinds.WatchItem) return;
        OpenRawXmlEditorWindow();
    }

    [RelayCommand]
    private void ApplyXmlEditor()
    {
        // Kept for backward compatibility � delegates to the window flow
        if (SelectedNode?.NodeKind != NodeKinds.WatchItem) return;
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
        if (SelectedNode?.NodeKind != NodeKinds.WatchItem) return;
        OpenRawXmlEditorWindow();
    }

    /// <summary>Opens the standalone Raw XML Editor window for the selected WatchItem.</summary>
    private void OpenRawXmlEditorWindow()
    {
        if (SelectedNode?.NodeKind != NodeKinds.WatchItem) return;
        if (SelectedNode.ModelObject is not WatchItemConfig oldWi) return;

        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchItem(oldWi);

        var editorAgents = GetXmlEditorAgents();
        var editorVm = new WatchItemXmlEditorViewModel(
            xml, _config, editorAgents.Select(agent => agent.Name).ToArray(), agentStatuses: editorAgents);
        editorVm.WindowTitle = $"WatchItem XML Editor � {oldWi.Tag}";

        var editorWindow = new Views.RawXmlEditorWindow(editorVm);

        // Set owner to main window for CenterOwner positioning. FindOwnerWindow
        // returns only a window that has actually been shown, preventing the WPF
        // "Cannot set Owner property to a Window that has not been shown
        // previously." crash.
        if (FindOwnerWindow() is { } mainWindow && !ReferenceEquals(mainWindow, editorWindow))
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            using var edit = BeginEdit($"Edit XML ({oldWi.Tag})");
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
                // The watcher captured the old WatchItem object; without this a file drop runs the pre-edit pipeline.
                _watcherManager.ApplyDiff(_config.WatchItems);
                IsDirty = true;
                RunValidation();
                XmlEditorStatus = "Applied - press Save (Ctrl+S) to write WatchList.xml.";
                IsXmlEditorOpen = false;
                AddLog($"WatchItem updated via XML editor: {parsed.Tag} (unsaved)", LogSeverity.Success);
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

        var editorAgents = GetXmlEditorAgents();
        var editorVm = new WatchItemXmlEditorViewModel(
            xml, _config, editorAgents.Select(agent => agent.Name).ToArray(), agentStatuses: editorAgents);
        editorVm.WindowTitle = "WatchList XML Editor � Full";

        var editorWindow = new Views.RawXmlEditorWindow(editorVm);
        if (FindOwnerWindow() is { } mainWindow && !ReferenceEquals(mainWindow, editorWindow))
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            using var edit = BeginEdit("Edit WatchList XML");
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
                _config.GlobalVariablesFile = newConfig.GlobalVariablesFile;
                _config.FilePath = VocabFilePath;

                _executor.LoadTemplates(_config.Templates);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RebuildAllTrees();
                        RunValidation();
                        StatusMessage = $"{_config.WatchItems.Count} WatchItems, {_config.Templates.Count} Templates";
                    }
                    catch (Exception ex)
                    {
                        _appLogger.Error("UI", "Failed to rebuild tree from XML editor", ex);
                    }
                });

                IsDirty = true;
                AddLog("? WatchList updated from XML editor (unsaved - press Save to write the file).", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                AddLog($"? WatchList XML apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    private IReadOnlyCollection<XmlEditorAgent> GetXmlEditorAgents() =>
        RegisteredAgents.Select(agent =>
        {
            var status = agent.ConnectionStatus != "Online"
                ? agent.ConnectionStatus
                : agent.AgentState.Contains("reboot", StringComparison.OrdinalIgnoreCase)
                    ? "Reboot required"
                    : agent.AgentState.Contains("running", StringComparison.OrdinalIgnoreCase)
                      || agent.AgentState.Contains("busy", StringComparison.OrdinalIgnoreCase)
                        ? "Busy"
                        : "Online";
            return new XmlEditorAgent(agent.Name, status);
        }).ToArray();
}
