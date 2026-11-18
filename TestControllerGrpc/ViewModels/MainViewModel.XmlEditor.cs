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
        // Kept for backward compatibility - delegates to the window flow
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
        if (!EnsureCanEdit(SelectedNode)) return;

        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchItem(oldWi);

        var editorAgents = GetXmlEditorAgents();
        var editorVm = new WatchItemXmlEditorViewModel(
            xml, _config, editorAgents.Select(agent => agent.Name).ToArray(), agentStatuses: editorAgents)
        {
            TabTitle = $"Pipeline: {oldWi.Tag}",
        };
        var session = new PipelineEditorSessionViewModel(editorVm, _config, editorAgents)
        {
            WindowTitle = $"Pipeline Editor - {oldWi.Tag}",
            TemplateEditBlocked = () => EditBlockedReason(Editing.EditTarget.Template),
            LatestSavedXml = tab => LatestSavedXml(tab, oldWi.Tag),
        };

        var editorWindow = new Views.RawXmlEditorWindow(session);

        // Set owner to main window for CenterOwner positioning. FindOwnerWindow
        // returns only a window that has actually been shown, preventing the WPF
        // "Cannot set Owner property to a Window that has not been shown
        // previously." crash.
        if (FindOwnerWindow() is { } mainWindow && !ReferenceEquals(mainWindow, editorWindow))
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && session.DialogAccepted)
        {
            if (session.TemplateResults.Count > 0 && !EnsureCanEdit(Editing.EditTarget.Template)) return;
            using var edit = BeginEdit($"Edit pipeline ({oldWi.Tag})");
            try
            {
                if (session.TemplateResults.Count > 0) ApplyTemplateChanges(session.TemplateResults);
                if (session.PipelineResultXml is null)
                {
                    RebuildAllTrees();
                    IsDirty = true;
                    RunValidation();
                    XmlEditorStatus = AppliedMessage($"{session.TemplateResults.Count} template(s)");
                    return;
                }

                var parsed = WatchListXmlParser.DeserializeWatchItem(session.PipelineResultXml);
                if (parsed is null)
                {
                    XmlEditorStatus = "Error: Root element must be <WatchItem>.";
                    AddLog("XML editor: Root element was not <WatchItem>", LogSeverity.Error);
                    return;
                }

                // Replace in model
                var idx = _config.WatchItems.IndexOf(oldWi);
                if (idx >= 0) _config.WatchItems[idx] = parsed;

                // Replace in tree; template changes also change every pipeline's Ref rows, so rebuild those.
                if (session.TemplateResults.Count > 0) RebuildAllTrees();
                else if (WatchListRoot is not null)
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
                XmlEditorStatus = AppliedMessage(session.TemplateResults.Count > 0
                    ? $"pipeline '{parsed.Tag}' and {session.TemplateResults.Count} template(s)"
                    : $"pipeline '{parsed.Tag}'");
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
        if (!EnsureCanEdit(Editing.EditTarget.WholeWatchList)) return;
        WriteBackAll();
        var xml = WatchListXmlParser.SerializeWatchList(_config);

        var editorAgents = GetXmlEditorAgents();
        var editorVm = new WatchItemXmlEditorViewModel(
            xml, _config, editorAgents.Select(agent => agent.Name).ToArray(), agentStatuses: editorAgents)
        {
            TabTitle = "WatchList",
        };
        var session = new PipelineEditorSessionViewModel(editorVm, _config, editorAgents)
        {
            WindowTitle = "WatchList Editor - Full",
            LatestSavedXml = _ => WatchListXmlParser.SerializeWatchList(_config),
        };

        var editorWindow = new Views.RawXmlEditorWindow(session);
        if (FindOwnerWindow() is { } mainWindow && !ReferenceEquals(mainWindow, editorWindow))
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && session.DialogAccepted && session.PipelineResultXml is { } resultXml)
        {
            using var edit = BeginEdit("Edit WatchList XML");
            try
            {
                var newConfig = WatchListXmlParser.DeserializeWatchList(resultXml);
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

    private static string AppliedMessage(string what) =>
        $"Applied {what} at {DateTime.Now:HH:mm:ss} - press Save (Ctrl+S) to write WatchList.xml (the previous file is kept as a timestamped backup).";

    /// <summary>The saved XML a Pipeline Editor tab was opened from, as it is now; null when it no longer exists.</summary>
    private string? LatestSavedXml(WatchItemXmlEditorViewModel tab, string pipelineTag)
    {
        if (!tab.IsTemplateTab)
            return _config.WatchItems.FirstOrDefault(w => string.Equals(w.Tag, pipelineTag, StringComparison.OrdinalIgnoreCase))
                is { } item ? WatchListXmlParser.SerializeWatchItem(item) : null;
        return _config.Templates.FirstOrDefault(t => string.Equals(t.ID, tab.TemplateOriginalId, StringComparison.OrdinalIgnoreCase))
            is { } template ? PipelineEditor.PipelineTemplateEdits.TemplateDocument(template) : null;
    }

    /// <summary>Writes template tabs into the Library: replaced by their original ID, or added when new.</summary>
    private void ApplyTemplateChanges(IReadOnlyList<PipelineEditorTemplateChange> changes)
    {
        foreach (var change in changes)
        {
            if (WatchListXmlParser.DeserializeTemplateList(change.Xml)?.FirstOrDefault() is not { } template) continue;
            var index = change.OriginalId is null ? -1
                : _config.Templates.FindIndex(t => string.Equals(t.ID, change.OriginalId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _config.Templates[index] = template;
            else _config.Templates.Add(template);
            AddLog($"Template '{template.ID}' {(index >= 0 ? "updated" : "created")} in the Pipeline Editor (unsaved)", LogSeverity.Success);
        }
        TemplateRoots.Clear();
        TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
        RebuildTemplateIds();
        _executor.LoadTemplates(_config.Templates);
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
