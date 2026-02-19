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
                    var dir = wi.Path.TrimEnd('\\', '/');
                    if (!Directory.Exists(dir))
                    {
                        _logger.LogWarning("WatchItem path does not exist: {Path}", dir);
                        continue;
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create watcher for {Path}", wi.Path);
                }
            }

            _logger.LogInformation("Active watchers: {Count}", _watchers.Count);
        }
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

    private void TearDown()
    {
        foreach (var w in _watchers)
            w.Watcher.Dispose();
        _watchers.Clear();
    }

    public int ActiveWatcherCount
    {
        get { lock (_lock) return _watchers.Count; }
    }

    public void Dispose() => TearDown();

    private sealed record ActiveWatcher(WatchItemConfig Config, FileSystemWatcher Watcher);
}
