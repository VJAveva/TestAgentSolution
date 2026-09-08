using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Diagnostics;

/// <summary>
/// TEMPORARY UI performance instrumentation — see docs/reliability/UIPerformance-CopilotPrompts.md (P02).
/// <para>Enable with <c>UiPerf:Enabled=true</c> in appsettings.json or environment variable
/// <c>UIPERF=1</c>. Output goes to <c>&lt;log dir&gt;\ui-perf-YYYYMMDD.log</c> and
/// <c>&lt;log dir&gt;\ui-binding-errors-YYYYMMDD.log</c>.</para>
/// <para>To remove: delete this file, <c>UiPerfScope.cs</c>, and the two call sites in App.xaml.cs.</para>
/// </summary>
public static class UiPerfDiagnostics
{
    private const int ReportIntervalSeconds = 5;

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, ScopeStats> Scopes = new(StringComparer.Ordinal);

    private static Stopwatch? _frameClock;
    private static long _lastFrameTicks;
    private static int _frameCount;
    private static double _frameTotalMs;
    private static double _frameWorstMs;

    private static long _probeCount;
    private static double _probeTotalMs;
    private static double _probeWorstMs;

    private static Timer? _probeTimer;
    private static Timer? _reportTimer;
    private static Dispatcher? _dispatcher;
    private static string _logPath = "";
    private static BindingErrorListener? _bindingListener;

    public static bool IsEnabled { get; private set; }

    /// <summary>Wires all four probes. Safe to call once; a second call is ignored.</summary>
    public static void Install(IConfiguration? configuration, Dispatcher dispatcher)
    {
        lock (Gate)
        {
            if (IsEnabled) return;

            var fromEnv = Environment.GetEnvironmentVariable("UIPERF");
            var enabled =
                string.Equals(fromEnv, "1", StringComparison.Ordinal) ||
                string.Equals(fromEnv, "true", StringComparison.OrdinalIgnoreCase) ||
                (configuration?.GetValue("UiPerf:Enabled", false) ?? false);

            if (!enabled) return;

            IsEnabled = true;
            _dispatcher = dispatcher;

            var dir = AppLogger.DefaultLogDirectory;
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, $"ui-perf-{DateTime.Now:yyyyMMdd}.log");

            InstallBindingTrace(dir);

            _frameClock = Stopwatch.StartNew();
            _lastFrameTicks = _frameClock.ElapsedTicks;
            CompositionTarget.Rendering += OnRendering;

            // Probe 2: posted from a background thread so it measures real UI-thread
            // starvation. A DispatcherTimer would be starved by the same blockage and
            // would under-report the wait.
            _probeTimer = new Timer(_ => PostResponsivenessProbe(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _reportTimer = new Timer(_ => Report(), null,
                TimeSpan.FromSeconds(ReportIntervalSeconds),
                TimeSpan.FromSeconds(ReportIntervalSeconds));

            Write($"UiPerfDiagnostics ENABLED. pid={Environment.ProcessId} report every {ReportIntervalSeconds}s.");
        }
    }

    public static void Uninstall()
    {
        lock (Gate)
        {
            if (!IsEnabled) return;
            IsEnabled = false;
            CompositionTarget.Rendering -= OnRendering;
            _probeTimer?.Dispose();
            _reportTimer?.Dispose();
            _bindingListener?.Detach();
        }
    }

    // ── Probe 1: frame time ─────────────────────────────────────────
    private static void OnRendering(object? sender, EventArgs e)
    {
        var clock = _frameClock;
        if (clock is null) return;

        var now = clock.ElapsedTicks;
        var deltaMs = (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
        _lastFrameTicks = now;

        // First tick after an idle gap is not a frame cost; ignore obvious gaps.
        if (deltaMs > 2000) return;

        _frameCount++;
        _frameTotalMs += deltaMs;
        if (deltaMs > _frameWorstMs) _frameWorstMs = deltaMs;
    }

    // ── Probe 2: UI-thread responsiveness ───────────────────────────
    private static void PostResponsivenessProbe()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;

        var posted = Stopwatch.GetTimestamp();
        dispatcher.InvokeAsync(() =>
        {
            var waitMs = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
            Interlocked.Increment(ref _probeCount);
            lock (Gate)
            {
                _probeTotalMs += waitMs;
                if (waitMs > _probeWorstMs) _probeWorstMs = waitMs;
            }
        }, DispatcherPriority.Background);
    }

    // ── Probe 4: scoped panel timing ────────────────────────────────
    /// <summary>Times a panel load/refresh path. Returns a no-op when disabled.</summary>
    public static UiPerfScope Measure(string scope) =>
        IsEnabled ? new UiPerfScope(scope) : default;

    internal static void RecordScope(string scope, double elapsedMs)
    {
        var stats = Scopes.GetOrAdd(scope, _ => new ScopeStats());
        lock (stats)
        {
            stats.Count++;
            stats.TotalMs += elapsedMs;
            if (elapsedMs > stats.WorstMs) stats.WorstMs = elapsedMs;
        }
    }

    // ── Probe 3: binding errors ─────────────────────────────────────
    private static void InstallBindingTrace(string dir)
    {
        var path = Path.Combine(dir, $"ui-binding-errors-{DateTime.Now:yyyyMMdd}.log");
        _bindingListener = new BindingErrorListener(path);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingListener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    // ── Reporting ───────────────────────────────────────────────────
    private static void Report()
    {
        var sb = new StringBuilder();

        lock (Gate)
        {
            if (_frameCount > 0)
            {
                sb.Append($"frames={_frameCount} avg={_frameTotalMs / _frameCount:F2}ms worst={_frameWorstMs:F2}ms");
                _frameCount = 0; _frameTotalMs = 0; _frameWorstMs = 0;
            }
            else
            {
                sb.Append("frames=0 (idle)");
            }

            var probes = Interlocked.Exchange(ref _probeCount, 0);
            if (probes > 0)
            {
                sb.Append($" | uiWait avg={_probeTotalMs / probes:F1}ms worst={_probeWorstMs:F1}ms n={probes}");
                _probeTotalMs = 0; _probeWorstMs = 0;
            }
        }

        foreach (var (name, stats) in Scopes)
        {
            lock (stats)
            {
                if (stats.Count == 0) continue;
                sb.Append($" | {name} n={stats.Count} avg={stats.TotalMs / stats.Count:F1}ms worst={stats.WorstMs:F1}ms");
                stats.Count = 0; stats.TotalMs = 0; stats.WorstMs = 0;
            }
        }

        var errors = _bindingListener?.TakeSnapshot();
        if (errors is { Count: > 0 })
        {
            var total = errors.Sum(kv => kv.Value);
            sb.Append($" | bindingErrors={total} distinct={errors.Count}");
        }

        Write(sb.ToString());
    }

    private static void Write(string line)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException) { /* diagnostics must never break the app */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ScopeStats
    {
        public int Count;
        public double TotalMs;
        public double WorstMs;
    }

    /// <summary>Counts distinct binding errors so a per-row error is visible as a rate.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly string _path;
        private readonly StringBuilder _pending = new();

        public BindingErrorListener(string path) => _path = path;

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            var text = _pending.ToString();
            _pending.Clear();
            if (text.Length == 0) return;

            _counts.AddOrUpdate(text, 1, (_, n) => n + 1);
            try { File.AppendAllText(_path, text + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public IReadOnlyDictionary<string, int> TakeSnapshot() => _counts;

        public void Detach() => PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
    }
}

/// <summary>Stopwatch scope used by <see cref="UiPerfDiagnostics.Measure"/>. Zero cost when disabled.</summary>
public readonly struct UiPerfScope : IDisposable
{
    private readonly string? _scope;
    private readonly long _start;

    internal UiPerfScope(string scope)
    {
        _scope = scope;
        _start = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_scope is null) return;
        var ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
        UiPerfDiagnostics.RecordScope(_scope, ms);
    }
}
