using System.IO;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Initialize parameter file editor ????????????????????????????????
public sealed partial class MainViewModel
{
    /// <summary>Browse for a parameter file and set it on the active Initialize node.</summary>
    [RelayCommand]
    private void BrowseParameterFile()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text Files|*.txt|All Files|*.*",
            Title = "Select Parameter File"
        };

        // Start in the current file's directory if possible
        if (!string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            var dir = Path.GetDirectoryName(ActiveEditNode.ParameterFile);
            if (dir is not null && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() != true) return;

        ActiveEditNode.ParameterFile = dlg.FileName;
        ActiveEditNode.ApplyToModel();
        ActiveEditNode.RefreshDisplayText();
        LoadParameterFileEntries(dlg.FileName);
        AddLog($"Parameter file selected: {dlg.FileName}");
    }

    /// <summary>Loads and displays all entries from a parameter file.</summary>
    [RelayCommand]
    private void LoadParameterFileFromNode()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile)) return;
        LoadParameterFileEntries(ActiveEditNode.ParameterFile);
    }

    private void LoadParameterFileEntries(string filePath)
    {
        ParameterFileEntries.Clear();
        ParameterFileStatus = "";

        if (!File.Exists(filePath))
        {
            ParameterFileStatus = $"File not found: {filePath}";
            return;
        }

        try
        {
            var entries = ParameterResolver.ParseParameterFile(filePath);
            foreach (var (key, value) in entries)
            {
                ParameterFileEntries.Add(new ParameterEntryViewModel { Key = key, Value = value });

                // Populate the shared token dictionary for UI display resolution
                TreeNodeViewModel.TokenValues[key] = value;
                if (key.StartsWith('_'))
                    TreeNodeViewModel.TokenValues[key[1..]] = value;
            }

            // Refresh resolved display text across all trees
            WatchListRoot?.RefreshResolvedTextRecursive();
            TemplateListRoot?.RefreshResolvedTextRecursive();

            ParameterFileStatus = $"Loaded {entries.Count} parameters from {Path.GetFileName(filePath)}";
            AddLog($"Loaded {entries.Count} parameters from {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            ParameterFileStatus = $"Error reading file: {ex.Message}";
        }
    }

    /// <summary>Adds a new empty parameter entry row.</summary>
    [RelayCommand]
    private void AddParameterEntry()
    {
        ParameterFileEntries.Add(new ParameterEntryViewModel
        {
            Key = "_NewKey",
            Value = "",
            IsNew = true
        });
    }

    /// <summary>Removes a parameter entry from the list.</summary>
    [RelayCommand]
    private void RemoveParameterEntry(ParameterEntryViewModel? entry)
    {
        if (entry is not null)
            ParameterFileEntries.Remove(entry);
    }

    /// <summary>Saves all parameter entries back to the parameter file.</summary>
    [RelayCommand]
    private void SaveParameterFile()
    {
        if (ActiveEditNode?.NodeKind != "Initialize") return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            ParameterFileStatus = "No parameter file path set. Use Browse to select a file.";
            return;
        }

        try
        {
            var entries = ParameterFileEntries
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => (e.Key, e.Value))
                .ToList();

            // Ensure directory exists
            var dir = Path.GetDirectoryName(ActiveEditNode.ParameterFile);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ParameterResolver.SaveParameterFile(ActiveEditNode.ParameterFile, entries);

            // Mark all as not new after save
            foreach (var e in ParameterFileEntries) e.IsNew = false;

            ParameterFileStatus = $"Saved {entries.Count} parameters to {Path.GetFileName(ActiveEditNode.ParameterFile)}";
            AddLog($"Saved parameter file: {ActiveEditNode.ParameterFile}", LogSeverity.Success);
        }
        catch (Exception ex)
        {
            ParameterFileStatus = $"Save error: {ex.Message}";
            AddLog($"Failed to save parameter file: {ex.Message}", LogSeverity.Error);
        }
    }
}
