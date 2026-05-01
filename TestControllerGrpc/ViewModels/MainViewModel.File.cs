using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ── File Operations (Open, Save, SaveAs, Refresh) ───────────────────
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void LoadVocabulary()
    {
        if (IsDirty)
        {
            var result = MessageBox.Show("Save changes before opening a new file?",
                "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes) SaveVocabulary();
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        { Filter = "XML Files|*.xml;*.txt|All|*.*", Title = "Open WatchList" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var config = _vocabMonitor.StartMonitoring(dlg.FileName);
            VocabFilePath = dlg.FileName;
            ApplyConfig(config);
            IsDirty = false;
            AddLog($"Loaded: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary file");
            AddLog($"{Helpers.LogIcons.Error} Load error: {ex.Message}", LogSeverity.Error);
        }
    }

    [RelayCommand]
    private void SaveVocabulary()
    {
        if (string.IsNullOrEmpty(VocabFilePath)) { SaveVocabularyAs(); return; }
        try
        {
            WriteBackAll();

            // Persist resolved AgentName values so the saved XML contains
            // the actual agent names instead of [Token] placeholders.
            ResolveAgentNamesInConfig(_config);

            // Suppress the file-watcher reload — we're saving our own in-memory state
            _vocabMonitor.SuppressNextReload();

            WatchListXmlParser.Save(_config, VocabFilePath);
            IsDirty = false;
            AddLog($"Saved: {VocabFilePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save vocabulary file");
            AddLog($"{Helpers.LogIcons.Error} Save error: {ex.Message}", LogSeverity.Error);
        }
    }

    [RelayCommand]
    private void SaveVocabularyAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        { Filter = "XML|*.xml|All|*.*", Title = "Save WatchList" };
        if (dlg.ShowDialog() != true) return;
        VocabFilePath = dlg.FileName;
        SaveVocabulary();
    }

    /// <summary>
    /// Refreshes the WatchList by reloading from disk and re-resolving all
    /// Initialize parameter tokens across the tree.
    /// </summary>
    [RelayCommand]
    private void RefreshWatchList()
    {
        if (string.IsNullOrEmpty(VocabFilePath) || !File.Exists(VocabFilePath))
        {
            LoadTokensFromConfig(_config);
            AddLog("Refreshed token resolution (no file loaded)");
            return;
        }

        try
        {
            var config = WatchListXmlParser.Load(VocabFilePath);
            config.FilePath = VocabFilePath;

            if (_sessionManager.HasAnyActiveExecution)
            {
                // DIFFERENTIAL reload — preserve running watchers
                _config = config;
                _executor.LoadTemplates(config.Templates);
                _watcherManager.ApplyDiff(config.WatchItems);

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RebuildAllTrees();
                    }
                    catch (Exception ex)
                    {
                        _appLogger.Error("UI", "Failed to rebuild tree during file refresh", ex);
                    }
                });

                IsDirty = false;
                AddLog($"Refreshed (differential — {_sessionManager.ActiveExecutionCount} execution(s) preserved): {VocabFilePath}",
                    LogSeverity.Success);
            }
            else
            {
                // FULL reload — no executions running
                ApplyConfig(config);
                IsDirty = false;
                AddLog($"Refreshed: {VocabFilePath}", LogSeverity.Success);
            }

            StatusMessage = $"Refreshed — {config.WatchItems.Count} WatchItems, {config.Templates.Count} Templates";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh vocabulary file");
            AddLog($"{Helpers.LogIcons.Error} Refresh error: {ex.Message}", LogSeverity.Error);
        }
    }

    /// <summary>Close the application.</summary>
    [RelayCommand]
    private void Close()
    {
        Application.Current?.Shutdown();
    }
}

