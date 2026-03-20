using System.IO;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Monitors the WatchList vocabulary XML file for changes.
/// When the file is created or modified, it reloads the configuration
/// and notifies the service to re-wire FileSystemWatchers.
///
/// Uses debouncing to avoid multiple reloads from rapid saves.
/// </summary>
public sealed class VocabularyMonitor : IVocabularyMonitor
{
    private readonly ILogger<VocabularyMonitor> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private string _filePath = "";
    private long _suppressUntilTicks;

    public event Action<WatchListConfig>? ConfigReloaded;

    public WatchListConfig? CurrentConfig { get; private set; }

    public VocabularyMonitor(ILogger<VocabularyMonitor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Suppresses file-change reloads for a short window. Call this before saving
    /// to prevent a save → detect change → reload → rebuild cycle.
    /// Uses a time window because FileSystemWatcher can fire multiple events per write.
    /// </summary>
    public void SuppressNextReload()
    {
        // Suppress all events arriving within the next 1 second.
        // FileSystemWatcher fires multiple events per write; this window covers them
        // without bleeding into genuinely separate file modifications.
        _suppressUntilTicks = DateTime.UtcNow.AddSeconds(1).Ticks;
    }

    /// <summary>
    /// Loads the initial configuration and starts monitoring for changes.
    /// </summary>
    public WatchListConfig StartMonitoring(string filePath)
    {
        _filePath = filePath;

        // Initial load
        CurrentConfig = LoadConfig(filePath);

        // Watch for changes
        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var file = Path.GetFileName(filePath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("Monitoring vocabulary file: {Path}", filePath);
        return CurrentConfig;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        DebounceReload();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // If the file was renamed to our target name, reload
        if (string.Equals(e.Name, Path.GetFileName(_filePath), StringComparison.OrdinalIgnoreCase))
            DebounceReload();
    }

    /// <summary>
    /// Debounces rapid file changes — waits 500ms after last change before reloading.
    /// </summary>
    private void DebounceReload()
    {
        // If we're within the suppression window (our own save), skip this reload cycle
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks))
            return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (token.IsCancellationRequested) return;

                _logger.LogInformation("Vocabulary file changed — reloading…");
                var config = LoadConfig(_filePath);
                CurrentConfig = config;
                ConfigReloaded?.Invoke(config);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload vocabulary file");
            }
        });
    }

    private WatchListConfig LoadConfig(string path)
    {
        // Retry in case file is still being written
        for (int i = 0; i < 3; i++)
        {
            try
            {
                return WatchListXmlParser.Load(path);
            }
            catch when (i < 2)
            {
                Thread.Sleep(200);
            }
        }
        return WatchListXmlParser.Load(path); // Let it throw on final try
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
