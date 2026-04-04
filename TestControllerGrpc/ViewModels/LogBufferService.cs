using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// High-performance log buffer that decouples log producers (pipeline executor,
/// gRPC agent output, file triggers) from the WPF UI thread.
///
/// <para><b>Problem solved:</b> With 100 agents each streaming stdout/stderr,
/// the original design called <c>Dispatcher.InvokeAsync</c> per line, producing
/// 100K+ dispatch queue items per second. Each one mutated an
/// <c>ObservableCollection</c> and triggered a ListBox layout pass.</para>
///
/// <para><b>Solution:</b> Producers write to a lock-free
/// <see cref="Channel{T}"/> (bounded, drop-oldest on overflow). A single
/// <see cref="DispatcherTimer"/> drains the channel in batches every 100ms,
/// adds them to the <see cref="RangeObservableCollection{T}"/> with one
/// <c>Reset</c> notification, and trims overflow in a single batch.</para>
///
/// <para><b>Architectural compatibility:</b>
/// <list type="bullet">
///   <item><b>WebAPI / WebClient:</b> Unaffected. They consume logs through
///   <c>IAppLogger</c> (file + ring buffer) in the Core project.</item>
///   <item><b>AgentDisplay:</b> Has its own <c>OutputLines</c> per agent.
///   This buffer only serves the Controller WPF app's execution log.</item>
///   <item><b>AgentGrpc:</b> Agent-side. No dependency.</item>
/// </list></para>
/// </summary>
public sealed class LogBufferService : IDisposable
{
    private readonly RangeObservableCollection<LogEntryViewModel> _target;
    private readonly RangeObservableCollection<LogEntryViewModel> _filteredTarget;
    private readonly Channel<LogEntryViewModel> _channel;
    private readonly DispatcherTimer _flushTimer;
    private Func<LogEntryViewModel, bool>? _filterPredicate;

    /// <summary>Maximum entries retained in the master log collection.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Maximum entries retained in the filtered view.</summary>
    public int MaxFilteredEntries { get; set; } = 10_000;

    /// <summary>How often the timer drains the channel (ms).</summary>
    public int FlushIntervalMs { get; init; } = 100;

    /// <summary>Max items drained per timer tick to cap UI-thread time.</summary>
    public int MaxBatchSize { get; init; } = 200;

    /// <summary>When true, new entries are buffered but not added to FilteredTarget.</summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Raised on the UI thread after each flush batch, with the count of new items.
    /// Used by the View to trigger auto-scroll without per-item CollectionChanged.
    /// </summary>
    public event Action<int>? BatchFlushed;

    public LogBufferService(
        RangeObservableCollection<LogEntryViewModel> target,
        RangeObservableCollection<LogEntryViewModel> filteredTarget)
    {
        _target = target;
        _filteredTarget = filteredTarget;

        // Bounded channel: if producers outpace the UI by >5000 entries,
        // the oldest are dropped. This prevents unbounded memory growth
        // when 100 agents flood output simultaneously.
        _channel = Channel.CreateBounded<LogEntryViewModel>(
            new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,   // Only the timer reads
                SingleWriter = false,  // Multiple threads write
            });

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs)
        };
        _flushTimer.Tick += OnFlush;
        _flushTimer.Start();
    }

    /// <summary>
    /// Enqueue a log entry from any thread. Lock-free, non-blocking.
    /// If the channel is full, the oldest unprocessed entry is dropped.
    /// </summary>
    public void Enqueue(LogEntryViewModel entry)
    {
        // TryWrite never blocks on BoundedChannelFullMode.DropOldest
        _channel.Writer.TryWrite(entry);
    }

    /// <summary>
    /// Sets the filter predicate used to decide which entries appear
    /// in the filtered collection. Pass <c>null</c> to show all.
    /// Thread-safe (called from UI thread when filter text changes).
    /// </summary>
    public void SetFilter(Func<LogEntryViewModel, bool>? predicate)
    {
        _filterPredicate = predicate;
    }

    /// <summary>
    /// Reapplies the current filter to the entire master collection,
    /// rebuilding the filtered collection in a single batch.
    /// Called when filter criteria change.
    /// </summary>
    public void ReapplyFilter()
    {
        _filteredTarget.Clear();

        var predicate = _filterPredicate;
        if (predicate is null)
        {
            _filteredTarget.AddRange(_target);
            return;
        }

        // Build filtered list in one pass, then add in batch
        var matching = new List<LogEntryViewModel>(_target.Count / 2);
        foreach (var entry in _target)
        {
            if (predicate(entry))
                matching.Add(entry);
        }

        if (matching.Count > 0)
            _filteredTarget.AddRange(matching);
    }

    /// <summary>Timer callback — drains the channel in batches on the UI thread.</summary>
    private void OnFlush(object? sender, EventArgs e)
    {
        var reader = _channel.Reader;
        var batch = new List<LogEntryViewModel>();

        while (batch.Count < MaxBatchSize && reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0) return;

        // Add all new entries in one notification
        _target.AddRange(batch);

        // Trim excess from the start in one notification
        var overflow = _target.Count - MaxEntries;
        if (overflow > 0)
            _target.TrimFromStart(overflow);

        // Add to filtered view (if not paused)
        if (!IsPaused)
        {
            var predicate = _filterPredicate;
            var filtered = predicate is null
                ? batch
                : batch.Where(predicate).ToList();

            if (filtered.Count > 0)
            {
                _filteredTarget.AddRange(filtered);

                var filteredOverflow = _filteredTarget.Count - MaxFilteredEntries;
                if (filteredOverflow > 0)
                    _filteredTarget.TrimFromStart(filteredOverflow);
            }
        }

        BatchFlushed?.Invoke(batch.Count);
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlush;
        _channel.Writer.TryComplete();
    }
}
