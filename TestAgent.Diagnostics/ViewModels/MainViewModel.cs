using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestAgent.Diagnostics.Models;
using TestAgent.Diagnostics.Services;

namespace TestAgent.Diagnostics.ViewModels;

public enum DiagView { Failures, Logs, Live }

/// <summary>
/// Shell view-model: owns both views over one parsed record set, the file load / reload,
/// the View A → View B link, theme switching, and the active-view toggle.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LogLoader _loader = new();
    private string[] _loadedFiles = Array.Empty<string>();

    // Live tail state.
    private readonly List<LogFileTail> _tails = new();
    private DispatcherTimer? _liveTimer;
    private int _ticksSinceFailureRefresh;
    private readonly List<LogRecord> _liveRecords = new();

    public FailuresViewModel Failures { get; } = new();
    public LogsViewModel Logs { get; } = new();
    public LiveStatusViewModel Live { get; } = new();

    public string[] Themes { get; } = { "Dark", "Light", "HighContrast" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailures))]
    [NotifyPropertyChangedFor(nameof(IsLogs))]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private DiagView _currentView = DiagView.Failures;

    [ObservableProperty] private string _selectedTheme = "Dark";
    [ObservableProperty] private string _status = "Open one or more log files to begin.";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _isLiveActive;

    public bool IsFailures => CurrentView == DiagView.Failures;
    public bool IsLogs => CurrentView == DiagView.Logs;
    public bool IsLive => CurrentView == DiagView.Live;

    public MainViewModel()
    {
        Failures.ViewLogsRequested += OnViewLogsRequested;
        Live.ViewLogsRequested += OnViewLogsRequested;
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
    private void ShowLive() => CurrentView = DiagView.Live;

    [RelayCommand]
    private void BackToFailure() => CurrentView = DiagView.Failures;

    [RelayCommand]
    private void OpenFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open log files",
            Filter = "Log & JSONL files (*.log;*.jsonl)|*.log;*.jsonl|Log files (*.log)|*.log|" +
                     "JSONL files (*.jsonl)|*.jsonl|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = DefaultLogDir(),
        };
        if (dlg.ShowDialog() != true) return;
        StopLive();
        _loadedFiles = dlg.FileNames;
        LoadFiles();
    }

    [RelayCommand]
    private void Reload()
    {
        if (IsLiveActive) return;
        if (_loadedFiles.Length == 0) { OpenFiles(); return; }
        LoadFiles();
    }

    /// <summary>Start tailing the newest live log files in the default directory.</summary>
    [RelayCommand]
    private void GoLive()
    {
        StopLive();

        var dir = DefaultLogDir();
        var files = LogLoader.LatestLiveFiles(dir);
        if (files.Count == 0)
        {
            Status = $"No live log files found in {dir}.";
            return;
        }

        // Seed from the current contents, then tail from the end.
        try
        {
            _liveRecords.Clear();
            _liveRecords.AddRange(_loader.Load(files));
        }
        catch (Exception ex)
        {
            Status = "Failed to start live: " + ex.Message;
            return;
        }

        Failures.SetRecords(_liveRecords);
        Logs.SetRecords(_liveRecords);
        Live.SetRecords(_liveRecords);
        Logs.AutoFollow = true;

        _tails.Clear();
        foreach (var f in files)
        {
            var tail = new LogFileTail(f);
            tail.SeekToEnd();
            _tails.Add(tail);
        }

        HasData = _liveRecords.Count > 0;
        IsLiveActive = true;
        CurrentView = DiagView.Live;
        Status = $"● LIVE \u00B7 tailing {files.Count} file(s) in {dir}";

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
    }

    /// <summary>Stop live tailing and freeze the current record set.</summary>
    [RelayCommand]
    private void StopLive()
    {
        if (_liveTimer is not null)
        {
            _liveTimer.Stop();
            _liveTimer.Tick -= OnLiveTick;
            _liveTimer = null;
        }
        _tails.Clear();
        if (IsLiveActive)
        {
            IsLiveActive = false;
            Status = $"Live stopped \u00B7 {_liveRecords.Count:N0} entries captured.";
        }
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        var batch = new List<LogRecord>();
        foreach (var tail in _tails)
        {
            try { batch.AddRange(tail.ReadNew()); }
            catch { /* file briefly locked / rolled over; retry next tick */ }
        }
        if (batch.Count == 0) return;

        _liveRecords.AddRange(batch);
        Logs.AppendRecords(batch);
        Live.Ingest(batch);

        // Re-analyzing failures is heavier; debounce to every ~3s.
        if (++_ticksSinceFailureRefresh >= 3)
        {
            _ticksSinceFailureRefresh = 0;
            Failures.SetRecords(_liveRecords);
        }

        Status = $"● LIVE \u00B7 {_liveRecords.Count:N0} entries \u00B7 " +
                 $"{Live.RunningCount} running / {Live.FailedCount} failed";
    }

    private void LoadFiles()
    {
        try
        {
            var records = _loader.Load(_loadedFiles);
            Failures.SetRecords(records);
            Logs.SetRecords(records);
            Live.SetRecords(records);
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
