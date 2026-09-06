using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Adapts <see cref="WatchListFileService"/> to the <see cref="IVocabularyMonitor"/> interface
/// so that the shared API controllers (which depend on IVocabularyMonitor) can work
/// in standalone WebApi mode where there is no live FileSystemWatcher.
///
/// Config is loaded from disk on demand. Hot-reload is not supported in standalone mode.
/// </summary>
public sealed class StandaloneVocabularyMonitor : IVocabularyMonitor
{
    private readonly WatchListFileService _fileService;
    private readonly IAppLogger _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _reloadGate = new();
    private WatchListConfig? _cached;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    public event Action<WatchListConfig>? ConfigReloaded;

    public StandaloneVocabularyMonitor(WatchListFileService fileService, IAppLogger logger)
    {
        _fileService = fileService;
        _logger = logger;
        _watcher = TryStartWatcher();
    }

    // Co-located deployments share the watchlist file with the controller; watch it so operator edits in the
    // WPF host invalidate this host's cache and reach the WebClient live (ConfigReloaded -> WatchListReloaded).
    private FileSystemWatcher? TryStartWatcher()
    {
        try
        {
            var path = _fileService.FilePath;
            var dir = Path.GetDirectoryName(path);
            var file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file) || !Directory.Exists(dir))
            {
                _logger.Warn("WatchListFile", $"Live watchlist reload disabled: directory not found for '{path}'.");
                return null;
            }
            var w = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            };
            w.Changed += OnFileChanged;
            w.Created += OnFileChanged;
            w.Renamed += OnFileChanged;
            w.EnableRaisingEvents = true;
            _logger.Info("WatchListFile", $"Watching '{path}' for live reload.");
            return w;
        }
        catch (Exception ex)
        {
            _logger.Warn("WatchListFile", $"Could not start watchlist file watcher: {ex.Message}");
            return null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // A single save raises several events; collapse them to one reload per burst.
        lock (_reloadGate)
        {
            if ((DateTime.UtcNow - _lastReloadUtc).TotalMilliseconds < 500) return;
            _lastReloadUtc = DateTime.UtcNow;
        }
        // Let the writer finish flushing before we re-read.
        _ = Task.Delay(250).ContinueWith(_ => Invalidate(), TaskScheduler.Default);
    }

    public WatchListConfig? CurrentConfig
    {
        get
        {
            if (_cached != null) return _cached;
            try
            {
                _cached = _fileService.Exists() ? _fileService.Load() : null;
            }
            catch { _cached = null; }
            return _cached;
        }
    }

    public WatchListConfig StartMonitoring(string filePath)
    {
        _cached = _fileService.Load();
        return _cached;
    }

    public void SuppressNextReload() { }

    /// <summary>Clears the cache so the next access reloads from disk; raises ConfigReloaded with the fresh config.</summary>
    public void Invalidate()
    {
        _cached = null;
        try
        {
            var fresh = CurrentConfig;
            if (fresh != null)
                ConfigReloaded?.Invoke(fresh);
        }
        catch (Exception ex)
        {
            _logger.Warn("WatchListFile", $"Reload after watchlist file change failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Renamed -= OnFileChanged;
        _watcher.Dispose();
    }
}
