using System.IO;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Creates and manages FileSystemWatcher instances for each WatchItem.
/// When a watched file event occurs (e.g., Renamed), triggers the
/// corresponding Event pipeline through ActionPipelineExecutor.
///
/// Supports hot-reload: when vocabulary changes, tears down all
/// watchers and recreates them from the new config.
/// </summary>
public sealed class FileWatcherManager : IDisposable
{
    private readonly ActionPipelineExecutor _executor;
    private readonly ILogger<FileWatcherManager> _logger;
    private readonly List<ActiveWatcher> _watchers = new();
    private readonly object _lock = new();
    private WatchListConfig _config = new();

    /// <summary>Raised when a file trigger fires.</summary>
    public event Action<string, string>? TriggerFired;  // watchPath, fileName

    /// <summary>Raised when trigger file metadata is parsed, carrying WatchItem tag and extracted values.</summary>
    public event Action<string, string, string>? TriggerMetadataParsed;  // watchItemTag, buildNumber, dropLocation

    /// <summary>Raised after trigger file is parsed, carrying all resolved parameters for UI token updates.</summary>
    public event Action<string, Dictionary<string, string>>? TriggerParametersLoaded;  // watchItemTag, parameters

    public FileWatcherManager(
        ActionPipelineExecutor executor,
        ILogger<FileWatcherManager> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Applies a new config: tears down existing watchers and creates new ones.
    /// </summary>
    public void ApplyConfig(WatchListConfig config)
    {
        lock (_lock)
        {
            _config = config;
            TearDown();

            foreach (var wi in config.WatchItems)
            {
                if (!wi.IsEnabled) continue;
                if (string.IsNullOrEmpty(wi.Path)) continue;

                try
                {
                    CreateAndAddWatcher(wi);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create watcher for {Path}", wi.Path);
                }
            }

            _logger.LogInformation("Active watchers: {Count}", _watchers.Count);
        }
    }

    // ── Differential watcher management ────────────────────────────────

    /// <summary>
    /// Adds or replaces a watcher for a single WatchItem. Safe to call
    /// while other watchers are running — only the target item is affected.
    /// </summary>
    public void AddOrUpdateWatcher(WatchItemConfig item)
    {
        lock (_lock)
        {
            RemoveWatcherByTag(item.Tag);

            if (!item.IsEnabled || string.IsNullOrWhiteSpace(item.Path)) return;

            try
            {
                CreateAndAddWatcher(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create watcher for {Path}", item.Path);
            }
        }
    }

    /// <summary>Removes the watcher for a specific WatchItem tag.</summary>
    public void RemoveWatcher(string tag)
    {
        lock (_lock) RemoveWatcherByTag(tag);
    }

    /// <summary>Checks if a watcher is active for the given tag.</summary>
    public bool HasWatcher(string tag)
    {
        lock (_lock)
            return _watchers.Any(w =>
                string.Equals(w.Config.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies a diff: removes watchers for deleted items, adds/updates watchers
    /// for new or changed items. Running watchers for unchanged items are untouched.
    /// </summary>
    public void ApplyDiff(List<WatchItemConfig> newItems)
    {
        lock (_lock)
        {
            var newTags = newItems
                .Select(i => i.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentTags = _watchers
                .Select(w => w.Config.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Remove watchers for deleted items
            foreach (var removed in currentTags.Except(newTags))
            {
                RemoveWatcherByTag(removed);
                _logger.LogInformation("Removed watcher for deleted item: {Tag}", removed);
            }

            // Add or update each item
            foreach (var item in newItems)
            {
                var existing = _watchers.FirstOrDefault(w =>
                    string.Equals(w.Config.Tag, item.Tag, StringComparison.OrdinalIgnoreCase));

                var needsUpdate = existing is null
                    || !string.Equals(existing.Config.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.Config.Filter, item.Filter, StringComparison.OrdinalIgnoreCase)
                    || existing.Config.IsEnabled != item.IsEnabled;

                if (needsUpdate)
                {
                    RemoveWatcherByTag(item.Tag);
                    if (item.IsEnabled && !string.IsNullOrWhiteSpace(item.Path))
                    {
                        try
                        {
                            CreateAndAddWatcher(item);
                            _logger.LogInformation("Updated watcher: {Tag} → {Path}", item.Tag, item.Path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update watcher for {Path}", item.Path);
                        }
                    }
                }
            }

            _logger.LogInformation("Active watchers after diff: {Count}", _watchers.Count);
        }
    }

    // ── Internal helpers ───────────────────────────────────────────────

    private void CreateAndAddWatcher(WatchItemConfig wi)
    {
        var dir = wi.Path.TrimEnd('\\', '/');
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("WatchItem path does not exist: {Path}", dir);
            return;
        }

        var fsw = new FileSystemWatcher(dir, wi.Filter)
        {
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        var capturedWi = wi;

        foreach (var evt in wi.Events)
        {
            var capturedEvt = evt;
            switch (evt.Type.ToLower())
            {
                case "renamed":
                    fsw.Renamed += (s, e) =>
                        OnTriggered(capturedWi, capturedEvt, e.FullPath, e.Name ?? "");
                    break;
                case "created":
                    fsw.Created += (s, e) =>
                        OnTriggered(capturedWi, capturedEvt, e.FullPath, e.Name ?? "");
                    break;
                case "changed":
                    fsw.Changed += (s, e) =>
                        OnTriggered(capturedWi, capturedEvt, e.FullPath, e.Name ?? "");
                    break;
            }
        }

        _watchers.Add(new ActiveWatcher(wi, fsw));
        _logger.LogInformation("Watching: {Path}\\{Filter}", dir, wi.Filter);
    }

    private void OnTriggered(WatchItemConfig wi, EventConfig evt, string fullPath, string fileName)
    {
        _logger.LogInformation("Trigger: {Path} → {File} ({Type})", wi.Path, fileName, evt.Type);
        TriggerFired?.Invoke(wi.Path, fileName);

        // Build execution context
        var ctx = new PipelineExecutionContext
        {
            WatchItemPath = wi.Path,
            TriggerFileName = fileName,
        };

        // Load parameters from trigger file
        ParameterResolver.LoadTriggerFile(ctx, fullPath);

        // Extract configured metadata fields from the Filter file into WatchItemConfig
        ParseTriggerFileMetadata(wi, ctx);

        // Notify UI of extracted metadata so tree nodes can be updated
        TriggerMetadataParsed?.Invoke(wi.Tag, wi.LastBuildNumber ?? "", wi.LastDropLocation ?? "");

        // Notify UI of all parameters for token resolution
        TriggerParametersLoaded?.Invoke(wi.Tag, ctx.Parameters.ToDictionary(p => p.Key, p => p.Value));

        // Execute pipeline on background thread
        _ = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteEventAsync(evt, ctx, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline execution failed for {Path}/{File}", wi.Path, fileName);
            }
        });
    }

    /// <summary>
    /// Extracts configured field names from the trigger file parameters
    /// into the WatchItemConfig's runtime properties and the execution context.
    /// Looks up both the configured field name and the underscore-prefixed variant
    /// (e.g. BuildNumber and _BuildNumber) since trigger files commonly use the _ prefix.
    /// </summary>
    private static void ParseTriggerFileMetadata(WatchItemConfig wi, PipelineExecutionContext ctx)
    {
        // Try configured field name first, then with underscore prefix
        if (TryGetParam(ctx, wi.BuildNumberField, out var buildNum))
        {
            wi.LastBuildNumber = buildNum;
            // Ensure canonical key is also available for downstream resolution
            ctx.Parameters["BuildNumber"] = buildNum;
            ctx.Parameters["_BuildNumber"] = buildNum;
        }

        if (TryGetParam(ctx, wi.DropLocationField, out var dropLoc))
        {
            wi.LastDropLocation = dropLoc;
            ctx.Parameters["DropLocation"] = dropLoc;
            ctx.Parameters["_DropLocation"] = dropLoc;
        }
    }

    /// <summary>
    /// Tries to get a parameter value by key, also checking the underscore-prefixed
    /// variant (e.g. "BuildNumber" also checks "_BuildNumber" and vice versa).
    /// </summary>
    private static bool TryGetParam(PipelineExecutionContext ctx, string fieldName, out string value)
    {
        if (ctx.Parameters.TryGetValue(fieldName, out value!))
            return true;
        // Try with underscore prefix if not already prefixed
        if (!fieldName.StartsWith('_') && ctx.Parameters.TryGetValue("_" + fieldName, out value!))
            return true;
        // Try without underscore prefix if already prefixed
        if (fieldName.StartsWith('_') && ctx.Parameters.TryGetValue(fieldName[1..], out value!))
            return true;
        value = "";
        return false;
    }

    private void TearDown()
    {
        foreach (var w in _watchers)
            w.Watcher.Dispose();
        _watchers.Clear();
    }

    private void RemoveWatcherByTag(string tag)
    {
        var toRemove = _watchers
            .Where(w => string.Equals(w.Config.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var w in toRemove)
        {
            w.Watcher.EnableRaisingEvents = false;
            w.Watcher.Dispose();
            _watchers.Remove(w);
        }
    }

    public int ActiveWatcherCount
    {
        get { lock (_lock) return _watchers.Count; }
    }

    public void Dispose() { lock (_lock) TearDown(); }

    private sealed record ActiveWatcher(WatchItemConfig Config, FileSystemWatcher Watcher);
}
