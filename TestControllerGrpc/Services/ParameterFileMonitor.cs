using System.IO;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Watches the parameter files an Initialize node points at, so a value edited in
/// <c>pipeline-config.json</c> reaches the tree labels without restarting the controller.
/// </summary>
/// <remarks>
/// <see cref="VocabularyMonitor"/> watches WatchList.xml and <see cref="FileWatcherManager"/>
/// watches the trigger folders - nothing watched the parameter files, so token display went stale
/// the moment a config was edited and only corrected on the next full tree rebuild.
/// Display-only: this never re-runs anything, it just re-resolves what is on screen.
/// </remarks>
public sealed class ParameterFileMonitor : IDisposable
{
    private readonly ILogger<ParameterFileMonitor>? _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    /// <summary>Raised, debounced, after any watched parameter file changes.</summary>
    public event Action? ParametersChanged;

    /// <summary>How long to wait for a burst of write events to settle.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    public ParameterFileMonitor(ILogger<ParameterFileMonitor>? logger = null) => _logger = logger;

    /// <summary>Files currently being watched. Exposed for tests and diagnostics.</summary>
    public IReadOnlyCollection<string> WatchedFiles
    {
        get { lock (_gate) return _files.ToList(); }
    }

    /// <summary>
    /// Replaces the watch set with <paramref name="filePaths"/>. Re-running with the same paths is
    /// cheap but not free, so callers pass the full set after a config load rather than per node.
    /// </summary>
    public void Watch(IEnumerable<string?> filePaths)
    {
        if (_disposed) return;

        var wanted = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            // An unresolved token in the path means the real file is not knowable yet.
            .Where(p => !p.Contains('['))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_gate)
        {
            if (_files.SetEquals(wanted)) return;

            DisposeWatchers();
            _files.Clear();

            foreach (var group in wanted.GroupBy(
                         p => Path.GetDirectoryName(p) ?? ".", StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(group.Key)) continue;

                try
                {
                    var w = new FileSystemWatcher(group.Key)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                    };
                    w.Changed += OnChanged;
                    w.Created += OnChanged;
                    w.Renamed += OnChanged;
                    _watchers.Add(w);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not watch parameter folder {Dir}", group.Key);
                    continue;
                }

                foreach (var f in group) _files.Add(f);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // One watcher serves a whole folder, so ignore files no Initialize node points at.
        lock (_gate)
        {
            if (!_files.Contains(e.FullPath)) return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounceCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = DebounceAsync(cts.Token);
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
            ParametersChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later write in the same burst.
        }
    }

    /// <summary>Raises the change event immediately. For tests, which cannot write fast enough to race a watcher.</summary>
    internal void RaiseForTest() => ParametersChanged?.Invoke();

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* already gone */ }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) DisposeWatchers();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
