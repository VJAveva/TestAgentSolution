using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Monitors the WatchList vocabulary XML file for changes.
/// When the file is created or modified, reloads the configuration
/// and notifies subscribers to re-wire FileSystemWatchers.
/// </summary>
public interface IVocabularyMonitor : IDisposable
{
    /// <summary>Raised when the vocabulary file changes and is successfully reloaded.</summary>
    event Action<WatchListConfig>? ConfigReloaded;

    /// <summary>The most recently loaded configuration, or null if not yet loaded.</summary>
    WatchListConfig? CurrentConfig { get; }

    /// <summary>Loads the initial configuration and starts monitoring for changes.</summary>
    WatchListConfig StartMonitoring(string filePath);

    /// <summary>
    /// Suppresses the next file-change reload. Call this before saving
    /// to prevent a save ? detect change ? reload ? rebuild cycle.
    /// </summary>
    void SuppressNextReload();
}
