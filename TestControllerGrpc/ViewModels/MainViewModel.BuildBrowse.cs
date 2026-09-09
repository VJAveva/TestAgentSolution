using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Build Browse: Browse Build folder + Select Build & Trigger ??????
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void BrowseBuild()
    {
        if (ActiveEditNode is null) return;

        var basePath = ActiveEditNode.BuildBasePath;

        var dlg = new OpenFolderDialog
        {
            Title = "Select Build Folder",
            InitialDirectory = string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath) ? "" : basePath,
        };

        try
        {
            if (dlg.ShowDialog() != true) return;
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException || ex is System.IO.DirectoryNotFoundException)
        {
            // Network path unreachable � retry without InitialDirectory
            AddLog($"Initial directory unreachable ({basePath}), opening default location. Error: {ex.Message}", LogSeverity.Warning);
            dlg = new OpenFolderDialog { Title = "Select Build Folder" };
            if (dlg.ShowDialog() != true) return;
        }

        var selectedPath = dlg.FolderName;
        var folderName = Path.GetFileName(selectedPath.TrimEnd('\\', '/'));
        var parentPath = Path.GetDirectoryName(selectedPath.TrimEnd('\\', '/'));

        // Set UI fields
        if (!string.IsNullOrEmpty(parentPath))
            ActiveEditNode.BuildBasePath = parentPath + "\\";

        // BuildNumberField/DropLocationField are parameter KEY NAMES that FileWatcherManager looks up
        // (ParseTriggerFileMetadata). Writing the selected build into them made the lookup miss forever.

        // Update per-pipeline build display
        ActiveEditNode.SelectedBuildPath = selectedPath;
        ActiveEditNode.LastBuildNumber = folderName;
        ActiveEditNode.LastDropLocation = selectedPath;
        if (ActiveEditNode.ModelObject is WatchItemConfig wiModel)
        {
            wiModel.LastBuildNumber = folderName;
            wiModel.LastDropLocation = selectedPath;
        }

        // Every Initialize file under this WatchItem. Updating only the first left later stages
        // (e.g. Sanity after Warm) installing the previous build.
        var paramFiles = FindInitializeParameterFiles(ActiveEditNode);
        if (paramFiles.Count == 0)
            AddLog("No Initialize node found under this WatchItem \u2014 Variables.txt not updated", LogSeverity.Warning);

        foreach (var paramFile in paramFiles)
        {
            try
            {
                // A layered JSON config has no flat _BuildNumber line, so the scan below would miss
                // it and APPEND CSV after the closing brace, leaving the file unparseable.
                if (ParameterResolver.IsLayeredConfig(paramFile))
                {
                    if (ParameterResolver.TryUpdateJsonBuild(paramFile, null, folderName, selectedPath))
                        AddLog($"Updated {paramFile}: _BuildNumber={folderName}, _DropLocation={selectedPath}");
                    else
                        AddLog($"Could not update layered config '{paramFile}'", LogSeverity.Error);
                    continue;
                }

                // Create file if it doesn't exist
                if (!File.Exists(paramFile))
                {
                    var dir = Path.GetDirectoryName(paramFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(paramFile, "");
                    AddLog($"Created parameter file: {paramFile}");
                }

                var lines = File.ReadAllLines(paramFile).ToList();
                bool bnFound = false, dlFound = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    // Handle both comma-separated (key,value) and equals-separated (key=value)
                    var sep = lines[i].Contains(',') ? ',' : '=';
                    var parts = lines[i].Split([sep], 2);
                    if (parts.Length < 2) continue;
                    var key = parts[0].Trim();

                    if (key.Equals("_BuildNumber", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"_BuildNumber{sep}{folderName}";
                        bnFound = true;
                    }
                    else if (key.Equals("_DropLocation", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"_DropLocation{sep}{selectedPath}";
                        dlFound = true;
                    }
                }

                // Add keys if not found � default to comma (canonical format)
                var delimiter = lines.Any(l => l.Contains(',')) ? "," : ",";
                if (!bnFound) lines.Add($"_BuildNumber{delimiter}{folderName}");
                if (!dlFound) lines.Add($"_DropLocation{delimiter}{selectedPath}");

                File.WriteAllLines(paramFile, lines);
                AddLog($"Updated {paramFile}: _BuildNumber={folderName}, _DropLocation={selectedPath}");
            }
            catch (Exception ex)
            {
                AddLog($"Failed to update parameter file '{paramFile}': {ex.Message}", LogSeverity.Error);
            }
        }

        AddLog($"Build selected: {folderName} at {selectedPath}");
    }

    /// <summary>
    /// Saves the build selected via Browse Build on this WatchItem to ALL pipelines:
    /// updates the global variables file and every Initialize parameter file in-place
    /// (only _BuildNumber and _DropLocation; all other parameters are preserved).
    /// </summary>
    [RelayCommand]
    private void SaveBuildToAllFiles()
    {
        if (ActiveEditNode is null) return;

        var buildNumber = ActiveEditNode.LastBuildNumber ?? "";
        var dropLocation = ActiveEditNode.LastDropLocation ?? "";

        if (string.IsNullOrWhiteSpace(buildNumber))
        {
            AddLog("No build selected \u2014 use Browse Build to pick a build folder first.", LogSeverity.Warning);
            return;
        }

        // Reuse the same in-place propagation used by the Global Variables editor:
        // writes _BuildNumber + _DropLocation to the global file and every pipeline's
        // parameter file, preserving all other parameters, and refreshes tokens/tree.
        PropagateGlobalBuildToAllParameterFiles(buildNumber, dropLocation);
        IsDirty = true;
    }

    /// <summary>
    /// Recursively searches the subtree to find the first Initialize node's ParameterFile path.
    /// Works at any nesting depth (WatchItem ? Event ? ActionGroup ? � ? Initialize).
    /// </summary>
    /// <summary>
    /// Every Initialize ParameterFile in the subtree, in declaration order, at any nesting depth
    /// (WatchItem -> Event -> ActionGroup -> ... -> Initialize). A pipeline may initialise several
    /// files; updating only the first leaves the later stages on the previous build.
    /// </summary>
    private static List<string> FindInitializeParameterFiles(TreeNodeViewModel node)
    {
        var files = new List<string>();
        Collect(node, files);
        return files;

        static void Collect(TreeNodeViewModel n, List<string> into)
        {
            if (n.NodeKind == NodeKinds.Initialize
                && !string.IsNullOrEmpty(n.ParameterFile)
                && !into.Contains(n.ParameterFile, StringComparer.OrdinalIgnoreCase))
            {
                into.Add(n.ParameterFile);
            }

            foreach (var child in n.Children)
                Collect(child, into);
        }
    }

    [RelayCommand]
    private void SelectBuildAndTrigger()
    {
        if (ActiveEditNode is null || string.IsNullOrEmpty(ActiveEditNode.SelectedBuildPath))
            return;

        var selectedPath = ActiveEditNode.SelectedBuildPath;
        var folderName = Path.GetFileName(selectedPath.TrimEnd('\\', '/'));
        var watchPath = ActiveEditNode.WatchPath;
        var filter = ActiveEditNode.Filter;
        var buildNumberField = ActiveEditNode.BuildNumberField;
        var dropLocationField = ActiveEditNode.DropLocationField;

        // Step 1: every Initialize parameter file under this WatchItem
        var paramFiles = FindInitializeParameterFiles(ActiveEditNode);

        // Step 2: Write BuildNumber and DropLocation to every parameter file
        var bnKey = string.IsNullOrEmpty(buildNumberField) ? "_BuildNumber" : buildNumberField;
        var dlKey = string.IsNullOrEmpty(dropLocationField) ? "_DropLocation" : dropLocationField;

        if (paramFiles.Count == 0)
            AddLog("No Initialize node found under this WatchItem \u2014 writing to trigger file only.", LogSeverity.Warning);

        foreach (var paramFile in paramFiles.Where(File.Exists))
        {
            try
            {
                // Layered JSON has no flat key line to match, so the scan below would append CSV
                // after the closing brace and make the file unparseable.
                if (ParameterResolver.IsLayeredConfig(paramFile))
                {
                    if (ParameterResolver.TryUpdateJsonBuild(paramFile, null, folderName, selectedPath))
                        AddLog($"Updated {paramFile}: _BuildNumber={folderName}, _DropLocation={selectedPath}");
                    else
                        AddLog($"Could not update layered config '{paramFile}'", LogSeverity.Error);
                    continue;
                }

                var lines = File.ReadAllLines(paramFile).ToList();
                bool bnFound = false, dlFound = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var sep = lines[i].Contains(',') ? ',' : '=';
                    var parts = lines[i].Split([sep], 2);
                    if (parts.Length < 2) continue;
                    var key = parts[0].Trim();

                    if (key.Equals(bnKey, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{bnKey}{sep}{folderName}";
                        bnFound = true;
                    }
                    else if (key.Equals(dlKey, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{dlKey}{sep}{selectedPath}";
                        dlFound = true;
                    }
                }

                // Add keys if not found � detect existing delimiter style
                var delimiter = lines.Any(l => l.Contains(',')) ? "," : ",";
                if (!bnFound) lines.Add($"{bnKey}{delimiter}{folderName}");
                if (!dlFound) lines.Add($"{dlKey}{delimiter}{selectedPath}");

                File.WriteAllLines(paramFile, lines);
                AddLog($"Updated {paramFile}: {bnKey}={folderName}, {dlKey}={selectedPath}");
            }
            catch (Exception ex)
            {
                AddLog($"Failed to update parameter file '{paramFile}': {ex.Message}", LogSeverity.Error);
            }
        }

        // Step 3: Create/rename the trigger file to fire the WatchItem
        if (!string.IsNullOrEmpty(watchPath) && !string.IsNullOrEmpty(filter))
        {
            try
            {
                if (!Directory.Exists(watchPath))
                    Directory.CreateDirectory(watchPath);

                var triggerFile = Path.Combine(watchPath, filter);
                var tempFile = triggerFile + ".tmp";

                var content = new[]
                {
                    $"{bnKey}={folderName}",
                    $"{dlKey}={selectedPath}",
                };

                File.WriteAllLines(tempFile, content);

                if (File.Exists(triggerFile))
                    File.Delete(triggerFile);
                File.Move(tempFile, triggerFile);

                AddLog($"Trigger file created: {triggerFile} (build={folderName})");
                AddLog($"WatchItem '{ActiveEditNode.Tag}' should fire automatically via FileWatcher.");
            }
            catch (Exception ex)
            {
                AddLog($"Failed to create trigger file: {ex.Message}", LogSeverity.Error);
            }
        }
    }
}
