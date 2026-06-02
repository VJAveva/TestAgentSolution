using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using TestControllerGrpc.ViewModels;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>
/// Dialog for setting a build number across all pipelines.
/// Browse Drop Location → pick build folder → Apply to All updates every parameter file.
/// Also allows editing the global variables file directly.
/// </summary>
public partial class GlobalVariablesEditorWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<ParameterEntryViewModel> _entries;
    private readonly string _initialBrowsePath;
    private readonly Action<string> _onFilePathChanged;
    private readonly Action<string, string>? _onBuildSelected;

    private string _filePath = "";
    private string _status = "";
    private string _dropLocation = "";
    private string _buildNumber = "";
    private bool _hasBuildSelected;

    public string FilePath
    {
        get => _filePath;
        private set { _filePath = value; RaisePropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; RaisePropertyChanged(); }
    }

    public string DropLocation
    {
        get => _dropLocation;
        set { _dropLocation = value; RaisePropertyChanged(); }
    }

    public string BuildNumber
    {
        get => _buildNumber;
        private set { _buildNumber = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasBuildSelected)); }
    }

    public bool HasBuildSelected => !string.IsNullOrWhiteSpace(_buildNumber);

    public ObservableCollection<ParameterEntryViewModel> Entries => _entries;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Creates the Global Variables editor dialog.
    /// </summary>
    /// <param name="filePath">Current global variables file path.</param>
    /// <param name="entries">Loaded entries collection.</param>
    /// <param name="initialBrowsePath">Initial directory for the Drop Location browse dialog.</param>
    /// <param name="onFilePathChanged">Callback when global variables file path changes.</param>
    /// <param name="onBuildSelected">Callback to propagate build to all parameter files (buildNumber, dropLocation).</param>
    public GlobalVariablesEditorWindow(
        string filePath,
        ObservableCollection<ParameterEntryViewModel> entries,
        string initialBrowsePath,
        Action<string> onFilePathChanged,
        Action<string, string>? onBuildSelected = null)
    {
        _entries = entries;
        _initialBrowsePath = initialBrowsePath;
        _onFilePathChanged = onFilePathChanged;
        _onBuildSelected = onBuildSelected;
        _filePath = filePath;

        // Pre-populate DropLocation and BuildNumber from existing entries
        var bnEntry = entries.FirstOrDefault(e => e.Key.Equals("_BuildNumber", StringComparison.OrdinalIgnoreCase));
        var dlEntry = entries.FirstOrDefault(e => e.Key.Equals("_DropLocation", StringComparison.OrdinalIgnoreCase));
        if (bnEntry is not null) _buildNumber = bnEntry.Value;
        if (dlEntry is not null) _dropLocation = dlEntry.Value;

        InitializeComponent();
        DataContext = this;

        UpdateStatus();
    }

    /// <summary>Browse to select a build folder from the drop location (network path).</summary>
    private void BrowseDropLocation_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Build Folder"
        };

        // Use current DropLocation or initial browse path as starting point
        var startDir = !string.IsNullOrWhiteSpace(DropLocation) && Directory.Exists(DropLocation)
            ? DropLocation
            : _initialBrowsePath;

        if (!string.IsNullOrWhiteSpace(startDir) && Directory.Exists(startDir))
            dlg.InitialDirectory = startDir;

        try
        {
            if (dlg.ShowDialog(this) != true) return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Network path unreachable — retry without InitialDirectory
            dlg = new OpenFolderDialog { Title = "Select Build Folder" };
            if (dlg.ShowDialog(this) != true) return;
        }

        var selectedPath = dlg.FolderName;
        var folderName = Path.GetFileName(selectedPath.TrimEnd('\\', '/'));

        DropLocation = selectedPath;
        BuildNumber = folderName;

        // Update _BuildNumber and _DropLocation in the entries grid
        SetEntryValue("_BuildNumber", folderName);
        SetEntryValue("_DropLocation", selectedPath);

        Status = $"Build selected: {folderName}";
    }

    /// <summary>Updates or adds a key in the entries collection.</summary>
    private void SetEntryValue(string key, string value)
    {
        var existing = _entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            _entries.Add(new ParameterEntryViewModel { Key = key, Value = value });
        }
    }

    /// <summary>
    /// Apply selected build to ALL WatchItem parameter files.
    /// Only updates _BuildNumber and _DropLocation in each file — all other parameters are preserved.
    /// </summary>
    private void ApplyToAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BuildNumber))
        {
            Status = "No build selected. Use Browse to select a build folder.";
            return;
        }

        // Propagate ONLY _BuildNumber and _DropLocation to all parameter files (in-place update)
        // Does NOT rewrite or delete other parameters in the files
        _onBuildSelected?.Invoke(BuildNumber, DropLocation);

        Status = $"✓ Build '{BuildNumber}' applied to all pipelines";
    }

    /// <summary>Browse for the global variables .txt file.</summary>
    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text Files|*.txt|All Files|*.*",
            Title = "Select Global Variables File"
        };

        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        else if (!string.IsNullOrWhiteSpace(_initialBrowsePath) && Directory.Exists(_initialBrowsePath))
        {
            dlg.InitialDirectory = _initialBrowsePath;
        }

        if (dlg.ShowDialog(this) != true) return;

        FilePath = dlg.FileName;
        _onFilePathChanged(dlg.FileName);

        // Reload entries from new file
        _entries.Clear();
        if (File.Exists(dlg.FileName))
        {
            try
            {
                var parsed = ParameterResolver.ParseParameterFile(dlg.FileName);
                foreach (var (key, value) in parsed)
                    _entries.Add(new ParameterEntryViewModel { Key = key, Value = value });

                // Re-populate build fields from loaded entries
                var bn = _entries.FirstOrDefault(x => x.Key.Equals("_BuildNumber", StringComparison.OrdinalIgnoreCase));
                var dl = _entries.FirstOrDefault(x => x.Key.Equals("_DropLocation", StringComparison.OrdinalIgnoreCase));
                if (bn is not null) BuildNumber = bn.Value;
                if (dl is not null) DropLocation = dl.Value;
            }
            catch { /* will show in status */ }
        }

        UpdateStatus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _entries.Add(new ParameterEntryViewModel { Key = "_NewKey", Value = "", IsNew = true });
        UpdateStatus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is ParameterEntryViewModel entry)
        {
            _entries.Remove(entry);
            UpdateStatus();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveEntriesToFile();
    }

    private void SaveEntriesToFile()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "No file path set. Use 'Browse…' to select a variables file.";
            return;
        }

        try
        {
            var entries = _entries
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => (x.Key, x.Value))
                .ToList();

            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ParameterResolver.SaveParameterFile(FilePath, entries);

            foreach (var entry in _entries) entry.IsNew = false;
            Status = $"Saved {entries.Count} variable(s) to {Path.GetFileName(FilePath)}";
        }
        catch (Exception ex)
        {
            Status = $"Save error: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void UpdateStatus()
    {
        Status = string.IsNullOrWhiteSpace(FilePath)
            ? "No variables file selected"
            : $"{_entries.Count} variable(s) — {Path.GetFileName(FilePath)}";
    }
}
