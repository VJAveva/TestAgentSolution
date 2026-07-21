using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels;

// ── Global Variables: Set same build for all pipelines ───────────────
public sealed partial class MainViewModel
{
    [ObservableProperty] private ObservableCollection<ParameterEntryViewModel> _globalVariableEntries = new();
    [ObservableProperty] private string _globalVariablesStatus = "";
    [ObservableProperty] private string _globalVariablesFilePath = "";

    /// <summary>
    /// Opens the Global Variables editor dialog.
    /// The dialog provides a Browse button for the Drop Location — selecting a build folder
    /// extracts the build number and propagates _BuildNumber + _DropLocation to ALL
    /// WatchItem Initialize parameter files AND the global variables file.
    /// </summary>
    [RelayCommand]
    private void EditGlobalVariables()
    {
        GlobalVariablesFilePath = _config.GlobalVariablesFile;
        LoadGlobalVariables();

        // Determine initial browse directory from BuildBasePath of first WatchItem (or config dir)
        var initialBrowsePath = GetBuildBasePathForGlobalBrowse();

        var dlg = new GlobalVariablesEditorWindow(
            _config.GlobalVariablesFile,
            GlobalVariableEntries,
            initialBrowsePath,
            onFilePathChanged: newPath =>
            {
                _config.GlobalVariablesFile = newPath;
                GlobalVariablesFilePath = newPath;
                IsDirty = true;
            },
            onBuildSelected: (buildNumber, dropLocation) =>
            {
                // Propagate to ALL WatchItem parameter files
                PropagateGlobalBuildToAllParameterFiles(buildNumber, dropLocation);

                // Refresh tokens and tree immediately so the treeview shows updated values
                LoadTokensFromConfig(_config);
            });

        if (FindOwnerWindow() is { } mainWindow
            && !ReferenceEquals(mainWindow, dlg))
        {
            dlg.Owner = mainWindow;
        }

        dlg.ShowDialog();

        // After dialog closes, refresh tokens
        LoadTokensFromConfig(_config);
    }

    /// <summary>
    /// Propagates the selected build number and drop location to ALL WatchItem
    /// Initialize parameter files, AND to the global variables file.
    /// Only scans each file and updates _BuildNumber + _DropLocation in-place.
    /// All other parameters in each file are preserved untouched.
    /// </summary>
    private void PropagateGlobalBuildToAllParameterFiles(string buildNumber, string dropLocation)
    {
        int updatedCount = 0;
        var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Update the global variables file (in-place, only two keys)
        if (!string.IsNullOrWhiteSpace(_config.GlobalVariablesFile))
        {
            UpdateParameterFileWithBuild(_config.GlobalVariablesFile, buildNumber, dropLocation);
            updatedPaths.Add(_config.GlobalVariablesFile);
            updatedCount++;
        }

        // 2) Update all WatchItem Initialize parameter files (in-place, only two keys)
        foreach (var wi in _config.WatchItems)
        {
            foreach (var ev in wi.Events)
            {
                foreach (var paramFile in CollectParameterFiles(ev.Children))
                {
                    // Avoid updating same file twice (multiple WatchItems may share a file)
                    if (updatedPaths.Contains(paramFile)) continue;
                    UpdateParameterFileWithBuild(paramFile, buildNumber, dropLocation);
                    updatedPaths.Add(paramFile);
                    updatedCount++;
                }
            }
        }

        // 3) Also update template Initialize parameter files
        foreach (var t in _config.Templates)
        {
            foreach (var paramFile in CollectParameterFiles(t.Children))
            {
                if (updatedPaths.Contains(paramFile)) continue;
                UpdateParameterFileWithBuild(paramFile, buildNumber, dropLocation);
                updatedPaths.Add(paramFile);
                updatedCount++;
            }
        }

        // 4) Update each WatchItem's model and tree node so the UI shows
        //    the correct per-pipeline build number and drop location.
        foreach (var wi in _config.WatchItems)
        {
            wi.LastBuildNumber = buildNumber;
            wi.LastDropLocation = dropLocation;
        }
        if (WatchListRoot is not null)
        {
            foreach (var child in WatchListRoot.Children)
            {
                if (child.NodeKind == NodeKinds.WatchItem)
                {
                    child.LastBuildNumber = buildNumber;
                    child.LastDropLocation = dropLocation;
                }
            }
        }

        // 5) Refresh tokens in UI
        LoadTokensFromConfig(_config);

        AddLog($"Global build set: {buildNumber} → updated {updatedCount} parameter file(s)", LogSeverity.Success);
    }

    /// <summary>Collects all ParameterFile paths from Initialize nodes in the subtree.</summary>
    private static List<string> CollectParameterFiles(List<IActionNode> children)
    {
        var files = new List<string>();
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
                files.Add(init.ParameterFile);
            else if (child is ActionGroupConfig ag)
                files.AddRange(CollectParameterFiles(ag.Children));
        }
        return files;
    }

    /// <summary>
    /// Scans a parameter file line-by-line and updates ONLY _BuildNumber and _DropLocation.
    /// All other lines (comments, blank lines, other parameters) are preserved exactly as-is.
    /// If the file doesn't exist, creates it with just those two entries.
    /// </summary>
    private void UpdateParameterFileWithBuild(string filePath, string buildNumber, string dropLocation)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(filePath))
            {
                // File doesn't exist — create with just the two build entries
                File.WriteAllLines(filePath, new[]
                {
                    $"_BuildNumber,{buildNumber}",
                    $"_DropLocation,{dropLocation}"
                });
                return;
            }

            // Read ALL lines (preserving comments, blank lines, other parameters)
            var lines = File.ReadAllLines(filePath).ToList();
            bool bnFound = false, dlFound = false;

            for (int i = 0; i < lines.Count; i++)
            {
                // Skip comments and blank lines — leave them untouched
                if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TrimStart().StartsWith('#'))
                    continue;

                // Detect delimiter style used in this line (comma or equals)
                var sep = lines[i].Contains(',') ? ',' : '=';
                var parts = lines[i].Split([sep], 2);
                if (parts.Length < 2) continue;
                var key = parts[0].Trim();

                // Only update these two keys — everything else is left as-is
                if (key.Equals("_BuildNumber", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"_BuildNumber{sep}{buildNumber}";
                    bnFound = true;
                }
                else if (key.Equals("_DropLocation", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"_DropLocation{sep}{dropLocation}";
                    dlFound = true;
                }
            }

            // Append if not found — detect existing delimiter style in file
            var delimiter = lines.Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && l.Contains(',')) ? "," : ",";
            if (!bnFound) lines.Add($"_BuildNumber{delimiter}{buildNumber}");
            if (!dlFound) lines.Add($"_DropLocation{delimiter}{dropLocation}");

            // Write back all lines — preserves every other parameter, comment, and blank line
            File.WriteAllLines(filePath, lines);
        }
        catch (Exception ex)
        {
            _appLogger.Warn("GlobalVars", $"Failed to update '{filePath}': {ex.Message}");
        }
    }

    /// <summary>Gets the best initial directory for the global build browse dialog.</summary>
    private string GetBuildBasePathForGlobalBrowse()
    {
        // Try first WatchItem's BuildBasePath
        foreach (var wi in _config.WatchItems)
        {
            if (WatchListRoot is null) break;
            foreach (var child in WatchListRoot.Children)
            {
                if (!string.IsNullOrWhiteSpace(child.BuildBasePath) && Directory.Exists(child.BuildBasePath))
                    return child.BuildBasePath;
            }
            break;
        }

        // Fall back to config file directory
        if (!string.IsNullOrWhiteSpace(_config.FilePath))
            return Path.GetDirectoryName(_config.FilePath) ?? "";

        return "";
    }

    /// <summary>Loads entries from the global variables file into the editor collection.</summary>
    private void LoadGlobalVariables()
    {
        GlobalVariableEntries.Clear();
        GlobalVariablesStatus = "";

        if (string.IsNullOrWhiteSpace(_config.GlobalVariablesFile))
        {
            GlobalVariablesStatus = "No global variables file configured.";
            return;
        }

        if (!File.Exists(_config.GlobalVariablesFile))
        {
            GlobalVariablesStatus = $"File not found: {_config.GlobalVariablesFile}";
            return;
        }

        try
        {
            var entries = ParameterResolver.ParseParameterFile(_config.GlobalVariablesFile);
            foreach (var (key, value) in entries)
                GlobalVariableEntries.Add(new ParameterEntryViewModel { Key = key, Value = value });

            GlobalVariablesStatus = $"Loaded {entries.Count} variable(s) from {Path.GetFileName(_config.GlobalVariablesFile)}";
        }
        catch (Exception ex)
        {
            GlobalVariablesStatus = $"Error reading file: {ex.Message}";
        }
    }
}
