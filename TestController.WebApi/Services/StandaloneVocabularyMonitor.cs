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
    private WatchListConfig? _cached;

    public event Action<WatchListConfig>? ConfigReloaded;

    public StandaloneVocabularyMonitor(WatchListFileService fileService)
    {
        _fileService = fileService;
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

    /// <summary>Clears the cache so the next access reloads from disk.</summary>
    public void Invalidate()
    {
        var old = _cached;
        _cached = null;
        if (old != null)
        {
            var fresh = CurrentConfig;
            if (fresh != null)
                ConfigReloaded?.Invoke(fresh);
        }
    }

    public void Dispose() { }
}
