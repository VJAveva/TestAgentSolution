using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestAgent.Diagnostics.Models;
using TestAgent.Diagnostics.Services;

namespace TestAgent.Diagnostics.ViewModels;

public enum DiagView { Failures, Logs }

/// <summary>
/// Shell view-model: owns both views over one parsed record set, the file load / reload,
/// the View A → View B link, theme switching, and the active-view toggle.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LogLoader _loader = new();
    private string[] _loadedFiles = Array.Empty<string>();

    public FailuresViewModel Failures { get; } = new();
    public LogsViewModel Logs { get; } = new();

    public string[] Themes { get; } = { "Dark", "Light", "HighContrast" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailures))]
    [NotifyPropertyChangedFor(nameof(IsLogs))]
    private DiagView _currentView = DiagView.Failures;

    [ObservableProperty] private string _selectedTheme = "Dark";
    [ObservableProperty] private string _status = "Open one or more log files to begin.";
    [ObservableProperty] private bool _hasData;

    public bool IsFailures => CurrentView == DiagView.Failures;
    public bool IsLogs => CurrentView == DiagView.Logs;

    public MainViewModel()
    {
        Failures.ViewLogsRequested += OnViewLogsRequested;
    }

    private void OnViewLogsRequested(LogFilterContext ctx)
    {
        Logs.ApplyContext(ctx);
        CurrentView = DiagView.Logs;
    }

    [RelayCommand]
    private void ShowFailures() => CurrentView = DiagView.Failures;

    [RelayCommand]
    private void ShowLogs() => CurrentView = DiagView.Logs;

    [RelayCommand]
    private void BackToFailure() => CurrentView = DiagView.Failures;

    [RelayCommand]
    private void OpenFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open log files",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = DefaultLogDir(),
        };
        if (dlg.ShowDialog() != true) return;
        _loadedFiles = dlg.FileNames;
        LoadFiles();
    }

    [RelayCommand]
    private void Reload()
    {
        if (_loadedFiles.Length == 0) { OpenFiles(); return; }
        LoadFiles();
    }

    private void LoadFiles()
    {
        try
        {
            var records = _loader.Load(_loadedFiles);
            Failures.SetRecords(records);
            Logs.SetRecords(records);
            HasData = records.Count > 0;
            Status = $"{records.Count:N0} entries from {_loadedFiles.Length} file(s) " +
                     $"\u00B7 {Failures.FailureCount} failing run(s)";
            CurrentView = DiagView.Failures;
        }
        catch (Exception ex)
        {
            Status = "Failed to load: " + ex.Message;
        }
    }

    partial void OnSelectedThemeChanged(string value) => ApplyTheme(value);

    private static void ApplyTheme(string theme)
    {
        var name = theme switch
        {
            "Light" => "LightTheme",
            "HighContrast" => "HighContrastTheme",
            _ => "DarkTheme",
        };
        var dicts = Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d =>
            d.Source is not null && d.Source.OriginalString.Contains("Theme.xaml"));
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Themes/{name}.xaml", UriKind.Relative),
        };
        if (existing is not null)
        {
            var idx = dicts.IndexOf(existing);
            dicts[idx] = replacement;
        }
        else
        {
            dicts.Insert(0, replacement);
        }
    }

    private static string DefaultLogDir()
    {
        const string shared = @"C:\TestControllerService\Logs";
        return Directory.Exists(shared) ? shared : Environment.CurrentDirectory;
    }
}
