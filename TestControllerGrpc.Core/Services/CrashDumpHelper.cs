using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Shared crash-capture infrastructure for all hosts (WPF, Agent, WebApi).
///
/// Capabilities:
///   1. Writes Win32 mini-dumps (.dmp) via MiniDumpWriteDump P/Invoke.
///   2. Installs global handlers (AppDomain + TaskScheduler) for any .NET host.
///   3. Maintains a rolling crash log and crash_summary.json per-app.
///   4. Auto-cleans dumps older than <see cref="DumpRetentionDays"/>.
///
/// Thread-safe. Never throws — all internal errors are swallowed so that
/// crash logging itself cannot kill the process.
/// </summary>
public static class CrashDumpHelper
{
    /// <summary>How long to keep crash dumps before auto-deletion.</summary>
    public const int DumpRetentionDays = 7;

    private static string _appName = "unknown";
    private static string _logDirectory = AppLogger.DefaultLogDirectory;
    private static IAppLogger? _logger;
    private static readonly object _lock = new();
    private static bool _installed;

    /// <summary>Path to the crash log file for this app.</summary>
    public static string CrashLogPath => Path.Combine(_logDirectory, $"{_appName}_crash.log");

    /// <summary>Path to the latest crash summary JSON.</summary>
    public static string CrashSummaryPath => Path.Combine(_logDirectory, "crash_summary.json");

    // ── P/Invoke for MiniDumpWriteDump (Windows only) ──────────────────

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        int processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    // MiniDumpWithFullMemory = 0x00000002
    private const int MiniDumpWithFullMemory = 0x00000002;

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Installs global crash handlers (AppDomain.UnhandledException +
    /// TaskScheduler.UnobservedTaskException) and configures the dump
    /// output directory. Call once at startup, before any async work.
    /// </summary>
    public static void InstallGlobalHandlers(string appName, string logDirectory, IAppLogger? logger = null)
    {
        lock (_lock)
        {
            if (_installed) return;
            _installed = true;

            _appName = appName;
            _logDirectory = logDirectory;
            _logger = logger;

            Directory.CreateDirectory(logDirectory);

            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;

            AppendCrashLog($"=== {_appName} Startup {DateTime.Now:O} PID={Environment.ProcessId} ===");

            // Clean old dumps on startup (best-effort)
            CleanOldDumps();
        }
    }

    /// <summary>
    /// Writes a mini-dump to the log directory. Windows-only; no-ops on
    /// other platforms. Returns the dump file path on success, null on failure.
    /// </summary>
    public static string? WriteMiniDump(string? reason = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dumpPath = Path.Combine(_logDirectory,
                $"crash_{_appName}_{timestamp}.dmp");

            using var process = Process.GetCurrentProcess();
            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var ok = MiniDumpWriteDump(
                process.Handle,
                process.Id,
                fs.SafeFileHandle.DangerousGetHandle(),
                MiniDumpWithFullMemory,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (ok)
            {
                AppendCrashLog($"[MiniDump] Written to: {dumpPath} (reason: {reason ?? "unspecified"})");
                return dumpPath;
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                AppendCrashLog($"[MiniDump] FAILED Win32 error={error}");
                return null;
            }
        }
        catch (Exception ex)
        {
            AppendCrashLog($"[MiniDump] Exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Records a crash event: writes to crash log, writes mini-dump,
    /// updates crash_summary.json, and logs via IAppLogger.
    /// </summary>
    public static void RecordCrash(string source, Exception? exception, bool isTerminating = false)
    {
        var exText = exception?.ToString() ?? "(no exception)";
        var msg = $"[{source} IsTerminating={isTerminating}] {exText}";

        Debug.WriteLine(msg);
        AppendCrashLog(msg);

        _logger?.Error("Crash", $"[{source}] {exception?.GetType().Name}: {exception?.Message}", exception);

        // Write mini-dump for non-recoverable crashes
        string? dumpPath = null;
        if (isTerminating || !IsRecoverable(exception))
        {
            dumpPath = WriteMiniDump(source);
        }

        // Update crash summary
        WriteCrashSummary(source, exception, dumpPath);
    }

    /// <summary>
    /// Appends a line to the per-app crash log file.
    /// Never throws.
    /// </summary>
    public static void AppendCrashLog(string text)
    {
        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var safe = SecurityRedactor.Redact(text) ?? text;
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {safe}{Environment.NewLine}");
        }
        catch { /* never let logging crash us */ }
    }

    /// <summary>
    /// Returns the most recent crash summary, or null if no crash recorded.
    /// </summary>
    public static CrashSummary? GetLastCrashSummary()
    {
        try
        {
            if (!File.Exists(CrashSummaryPath)) return null;
            var json = File.ReadAllText(CrashSummaryPath);
            return JsonSerializer.Deserialize<CrashSummary>(json);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns a list of crash dump files in the log directory.
    /// </summary>
    public static IReadOnlyList<CrashDumpInfo> GetCrashDumps()
    {
        try
        {
            if (!Directory.Exists(_logDirectory)) return [];
            return Directory.GetFiles(_logDirectory, "crash_*.dmp")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Select(f => new CrashDumpInfo
                {
                    FileName = f.Name,
                    SizeMB = Math.Round(f.Length / 1024.0 / 1024.0, 2),
                    CreatedUtc = f.CreationTimeUtc,
                })
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Checks whether the exception is a known recoverable type
    /// (transient gRPC disconnect, task cancellation, etc.).
    /// </summary>
    public static bool IsRecoverable(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is OperationCanceledException) return true;
            if (cur.GetType().Name == "RpcException") return true;
            if (cur is AggregateException agg && agg.InnerExceptions.Any(inner => IsRecoverable(inner)))
                return true;
            if (cur.InnerException is null) break;
        }
        return false;
    }

    // ── Internal handlers ──────────────────────────────────────────────

    private static void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        RecordCrash("AppDomainUnhandled", e.ExceptionObject as Exception, e.IsTerminating);
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        RecordCrash("UnobservedTask", e.Exception);
        // Mark observed so the finalizer thread does not rethrow.
        e.SetObserved();
    }

    // ── Crash summary persistence ──────────────────────────────────────

    private static void WriteCrashSummary(string source, Exception? ex, string? dumpPath)
    {
        try
        {
            var summary = new CrashSummary
            {
                AppName = _appName,
                Source = source,
                Timestamp = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ExceptionType = ex?.GetType().FullName,
                ExceptionMessage = ex?.Message,
                DumpFilePath = dumpPath,
            };

            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CrashSummaryPath, json);
        }
        catch { /* best effort */ }
    }

    // ── Dump retention ─────────────────────────────────────────────────

    private static void CleanOldDumps()
    {
        try
        {
            if (!Directory.Exists(_logDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-DumpRetentionDays);
            foreach (var file in Directory.GetFiles(_logDirectory, "crash_*.dmp"))
            {
                if (File.GetCreationTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    AppendCrashLog($"[Cleanup] Deleted old dump: {Path.GetFileName(file)}");
                }
            }
        }
        catch { /* best effort */ }
    }
}

/// <summary>Summary of the most recent crash, persisted as JSON.</summary>
public record CrashSummary
{
    public string AppName { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string MachineName { get; init; } = "";
    public int ProcessId { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? DumpFilePath { get; init; }
}

/// <summary>Info about a crash dump file.</summary>
public record CrashDumpInfo
{
    public string FileName { get; init; } = "";
    public double SizeMB { get; init; }
    public DateTime CreatedUtc { get; init; }
}
