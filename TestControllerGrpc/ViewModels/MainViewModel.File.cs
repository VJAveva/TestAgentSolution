using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ── File Operations (Open, Save, SaveAs) ────────────────────────────
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

    /// <summary>Close the application.</summary>
    [RelayCommand]
    private void Close()
    {
        Application.Current?.Shutdown();
    }
}

