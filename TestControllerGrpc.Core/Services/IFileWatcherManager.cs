using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Creates and manages FileSystemWatcher instances for each WatchItem.
/// Triggers the corresponding Event pipeline when watched file events occur.
/// Supports hot-reload via ApplyConfig and differential updates via ApplyDiff.
/// </summary>
public interface IFileWatcherManager : IDisposable
{
    /// <summary>Raised when a file trigger fires (watchPath, fileName).</summary>
    event Action<string, string>? TriggerFired;

    /// <summary>Raised when trigger file metadata is parsed (watchItemTag, buildNumber, dropLocation).</summary>
    event Action<string, string, string>? TriggerMetadataParsed;

    /// <summary>Raised after trigger file is parsed with all resolved parameters (watchItemTag, parameters).</summary>
    event Action<string, Dictionary<string, string>>? TriggerParametersLoaded;

    /// <summary>Tears down existing watchers and creates new ones from config.</summary>
    void ApplyConfig(WatchListConfig config);

    /// <summary>Adds or replaces a watcher for a single WatchItem.</summary>
    void AddOrUpdateWatcher(WatchItemConfig item);

    /// <summary>Removes the watcher for a specific WatchItem tag.</summary>
    void RemoveWatcher(string tag);

    /// <summary>Checks if a watcher is active for the given tag.</summary>
    bool HasWatcher(string tag);

    /// <summary>Applies a diff: removes deleted, adds/updates changed watchers.</summary>
    void ApplyDiff(List<WatchItemConfig> newItems);

    /// <summary>Number of currently active file system watchers.</summary>
    int ActiveWatcherCount { get; }
}
