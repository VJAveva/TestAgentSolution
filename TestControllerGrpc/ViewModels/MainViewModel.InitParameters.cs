using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Initialize parameter file editor ????????????????????????????????
public sealed partial class MainViewModel
{
    private bool _loadingParameterFileEntries;
    private readonly List<ParameterEntryViewModel> _removedParameterEntries = [];

    /// <summary>Browse for a parameter file and set it on the active Initialize node.</summary>
    [RelayCommand]
    private void BrowseParameterFile()
    {
        if (ActiveEditNode?.NodeKind != NodeKinds.Initialize) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Parameter Files|*.json;*.txt|JSON Config|*.json|Text Files|*.txt|All Files|*.*",
            Title = "Select Parameter File"
        };

        var defaultDirectory = Path.Combine(AppContext.BaseDirectory, "Parameters");
        if (!Directory.Exists(defaultDirectory))
            defaultDirectory = Path.Combine(Environment.CurrentDirectory, "Parameters");

        if (Directory.Exists(defaultDirectory))
            dlg.InitialDirectory = defaultDirectory;

        if (!string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            var dir = Path.GetDirectoryName(ActiveEditNode.ParameterFile);
            if (dir is not null && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() != true) return;

        ActiveEditNode.ParameterFile = dlg.FileName;
        LoadParameterFileEntries(dlg.FileName, ActiveEditNode.Profile);
        ActiveEditNode.ApplyToModel();
        ActiveEditNode.RefreshDisplayText();
        WatchParameterFiles(_config);
        AddLog($"Parameter file selected: {dlg.FileName}");
    }

    [RelayCommand]
    private void OpenParameterFile()
    {
        if (ActiveEditNode?.NodeKind != NodeKinds.Initialize) return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile) || !File.Exists(ActiveEditNode.ParameterFile))
        {
            SetInitializeParameterFileStatus(ActiveEditNode.ModelObject as InitializeConfig,
                $"File not found: {ActiveEditNode.ParameterFile}", false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ActiveEditNode.ParameterFile,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ParameterFileStatus = $"Could not open '{ActiveEditNode.ParameterFile}': {ex.Message}";
            AddLog(ParameterFileStatus, LogSeverity.Error);
        }
    }

    /// <summary>Loads and displays all entries from a parameter file.</summary>
    [RelayCommand]
    private void LoadParameterFileFromNode()
    {
        if (ActiveEditNode?.NodeKind != NodeKinds.Initialize) return;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            SetInitializeParameterFileStatus(ActiveEditNode.ModelObject as InitializeConfig,
                "No ParameterFile is configured.", false);
            return;
        }

        LoadParameterFileEntries(ActiveEditNode.ParameterFile, ActiveEditNode.Profile);
        ActiveEditNode.ApplyToModel();
        ActiveEditNode.RefreshDisplayText();
        WatchParameterFiles(_config);
    }

    private void LoadParameterFileEntries(string filePath, string profile = "")
    {
        var node = ActiveEditNode;
        if (node?.NodeKind != NodeKinds.Initialize) return;

        _loadingParameterFileEntries = true;
        try
        {
            ParameterFileEntries.Clear();
            _removedParameterEntries.Clear();
            ParameterFileStatus = "";
            node.ProfileChoices.Clear();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                SetInitializeParameterFileStatus(node.ModelObject as InitializeConfig, "No ParameterFile is configured.", false);
                return;
            }

            if (!File.Exists(filePath))
            {
                SetInitializeParameterFileStatus(node.ModelObject as InitializeConfig, $"File not found: {filePath}", false);
                return;
            }

            List<(string Key, string Value, ParameterLayer Layer)> entries;
            if (ParameterResolver.IsLayeredConfig(filePath))
            {
                if (!ParameterResolver.TryReadJsonConfig(filePath, out var config, out var readFailure))
                {
                    SetInitializeParameterFileStatus(node.ModelObject as InitializeConfig,
                        readFailure ?? $"Parameter file '{filePath}' could not be read.", true);
                    return;
                }

                foreach (var name in config!.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    node.ProfileChoices.Add(name);

                var previousProfile = node.Profile;
                if (node.ProfileChoices.Count == 0)
                    node.Profile = "";
                else if (!node.ProfileChoices.Any(p => string.Equals(p, profile, StringComparison.OrdinalIgnoreCase)))
                    node.Profile = node.ProfileChoices[0];
                else
                    node.Profile = profile;
                if (!string.Equals(previousProfile, node.Profile, StringComparison.Ordinal))
                    IsDirty = true;

                var ctx = new PipelineExecutionContext();
                var pipelineTag = node.TokenScope;
                if (!ParameterResolver.TryLoadJsonConfig(ctx, filePath, node.Profile, pipelineTag, out var loadFailure))
                {
                    SetInitializeParameterFileStatus(node.ModelObject as InitializeConfig,
                        loadFailure ?? $"Parameter file '{filePath}' could not be loaded.", false);
                    return;
                }

                entries = ctx.Parameters
                    .Where(p => p.Key.StartsWith('_'))
                    .Select(p => (p.Key, p.Value, ParameterFileWriter.LayerFromRank(
                        ctx.ParameterRanks.TryGetValue(p.Key, out var rank) ? rank : (int)ParameterRank.Global)))
                    .ToList();
            }
            else
            {
                entries = ParameterResolver.ParseParameterFile(filePath)
                    .Select(e => (e.Key, e.Value, ParameterLayer.Global))
                    .ToList();
                node.Profile = "";
            }

            foreach (var (key, value, layer) in entries)
                ParameterFileEntries.Add(new ParameterEntryViewModel
                {
                    Key = key, Value = value, OriginalKey = key, OriginalValue = value, Layer = layer,
                });

            node.ApplyToModel();
            ParameterFileStatus = ParameterResolver.IsLayeredConfig(filePath)
                ? $"Loaded {entries.Count} resolved parameters from '{filePath}', profile '{node.Profile}'."
                : $"Loaded {entries.Count} parameters from '{filePath}'.";
            node.ParameterFileValidationMessage = ParameterFileStatus;
            node.IsParameterFileStale = false;
            node.ParameterFileHasError = false;
            RefreshInitializeTokenScope(node);
            AddLog(ParameterFileStatus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ParameterFileStatus = $"Error reading file: {ex.Message}";
            SetInitializeParameterFileStatus(node.ModelObject as InitializeConfig, ParameterFileStatus, true);
        }
        finally
        {
            _loadingParameterFileEntries = false;
        }
    }

    private void RefreshInitializeTokenScope(TreeNodeViewModel node)
    {
        var scope = node.TokenScope;
        if (scope is null) return;

        var pipeline = _config.WatchItems.FirstOrDefault(w =>
            string.Equals(w.Tag, scope, StringComparison.OrdinalIgnoreCase));
        if (pipeline is null) return;

        var previous = TreeNodeViewModel.SnapshotTokens();
        TreeNodeViewModel.ClearTokenScope(scope);
        foreach (var evt in pipeline.Events)
            LoadTokensFromChildren(evt.Children, scope, previous);

        WatchParameterFiles(_config);
        WatchListRoot?.RefreshResolvedTextRecursive();
        TemplateListRoot?.RefreshResolvedTextRecursive();
    }

    private void RefreshAllInitializeTokenScopes()
    {
        var previous = TreeNodeViewModel.SnapshotTokens();
        foreach (var pipeline in _config.WatchItems)
        {
            TreeNodeViewModel.ClearTokenScope(pipeline.Tag);
            foreach (var evt in pipeline.Events)
                LoadTokensFromChildren(evt.Children, pipeline.Tag, previous);
        }

        WatchParameterFiles(_config);
        WatchListRoot?.RefreshResolvedTextRecursive();
        TemplateListRoot?.RefreshResolvedTextRecursive();
    }

    private void SetInitializeParameterFileStatus(InitializeConfig? init, string message, bool considerLastGood)
    {
        var node = init is null ? null : WatchListRoot?.FindByModel(init) ?? TemplateListRoot?.FindByModel(init);
        var lastGood = considerLastGood && init is not null
            ? ParameterResolver.LastGoodJsonReadUtc(init.ParameterFile)
            : null;
        var hasFallbackValues = lastGood is not null
            && node?.TokenScope is { } scope
            && TreeNodeViewModel.TokensFor(scope).Count > 0;

        if (node is not null)
        {
            node.IsParameterFileStale = hasFallbackValues;
            node.ParameterFileHasError = true;
            node.ParameterFileValidationMessage = hasFallbackValues
                ? $"Using last good copy from {lastGood!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}; current file invalid: {message}"
                : message;
        }

        ParameterFileStatus = node?.ParameterFileValidationMessage ?? message;
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
        if (entry is not null && ParameterFileEntries.Remove(entry))
            _removedParameterEntries.Add(entry);
    }

    /// <summary>True when the Parameters grid holds edits that are not yet in the file.</summary>
    internal bool HasUnsavedParameterEdits =>
        ParameterGridEdits.HasChanges(ParameterFileEntries, _removedParameterEntries);

    /// <summary>Saves all parameter entries back to the parameter file.</summary>
    [RelayCommand]
    private void SaveParameterFile() => SaveParameterEdits();

    /// <summary>Writes grid edits to the parameter file. Returns false (and says why) when nothing was written.</summary>
    private bool SaveParameterEdits()
    {
        if (ActiveEditNode?.NodeKind != NodeKinds.Initialize) return false;
        if (string.IsNullOrWhiteSpace(ActiveEditNode.ParameterFile))
        {
            ParameterFileStatus = "No parameter file path set. Use Browse to select a file.";
            return false;
        }

        if (ParameterResolver.IsLayeredConfig(ActiveEditNode.ParameterFile))
            return SaveLayeredParameterEdits(ActiveEditNode);

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
            foreach (var e in ParameterFileEntries)
            {
                e.IsNew = false;
                e.OriginalKey = e.Key;
                e.OriginalValue = e.Value;
            }
            _removedParameterEntries.Clear();

            ParameterFileStatus = $"Saved {entries.Count} parameters to {ActiveEditNode.ParameterFile} at {DateTime.Now:HH:mm:ss}";
            AddLog($"Saved parameter file: {ActiveEditNode.ParameterFile}", LogSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            ParameterFileStatus = $"Save error: {ex.Message}";
            AddLog($"Failed to save parameter file: {ex.Message}", LogSeverity.Error);
            return false;
        }
    }

    private bool SaveLayeredParameterEdits(TreeNodeViewModel node)
    {
        var edits = ParameterGridEdits.Compute(ParameterFileEntries, _removedParameterEntries, node.Profile);
        if (edits.Count == 0)
        {
            ParameterFileStatus = "No parameter changes to save.";
            return true;
        }

        var result = ParameterFileWriter.SaveJsonEdits(node.ParameterFile, node.Profile, node.TokenScope, edits);
        if (!result.Success)
        {
            ParameterFileStatus = $"Save failed: {result.Message}";
            AddLog(ParameterFileStatus, LogSeverity.Error);
            return false;
        }

        // Re-read so the grid, the layer column and every [Token] label show what is now on disk.
        LoadParameterFileEntries(node.ParameterFile, node.Profile);
        ParameterFileStatus = result.Message;
        AddLog($"{result.Message} {edits.Count} change(s).", LogSeverity.Success);
        return true;
    }
}
