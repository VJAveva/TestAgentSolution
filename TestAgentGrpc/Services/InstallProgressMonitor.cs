using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TestAgentGrpc.Services;

/// <summary>
/// Monitors silent install progress from three sources:
/// 1. ILog*.log inside {GUID} subfolders under ArchestrA\Install\
/// 2. MSI*.log (UTF-16LE) inside the same {GUID} subfolders
/// 3. Windows EventLog MsiInstaller events (11707=success, 11708=fail)
///
/// Streams per-component install status to the gRPC execution channel.
/// </summary>
public sealed class InstallProgressMonitor : IDisposable
{
    private readonly ChannelWriter<ExecutionEvent> _channel;
    private readonly string _executionId;
    private CancellationTokenSource? _cts;

    private string _installRoot;
    private string? _msiLogFile;
    private long _msiLogPosition;
    private string? _ilogFile;
    private int _ilogLineCount;
    private DateTime _lastEventTime;
    private string _currentProduct = "";
    private int _productIndex;

    private readonly Dictionary<string, DateTime> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public int InstalledCount => _installed.Count;
    public int FailedCount => _failed.Count;

    /// <summary>
    /// Fires with each meaningful install event message so external consumers
    /// (e.g. the heartbeat task) can incorporate it into progress reporting.
    /// </summary>
    public event Action<string>? OnProgress;

    public InstallProgressMonitor(ChannelWriter<ExecutionEvent> channel, string executionId)
    {
        _channel = channel;
        _executionId = executionId;
        _installRoot = @"C:\Program Files (x86)\Common Files\ArchestrA\Install";
        _lastEventTime = DateTime.Now;
    }

    public void Start(string? installRoot = null, int pollMs = 5000)
    {
        if (!string.IsNullOrEmpty(installRoot))
            _installRoot = installRoot;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            Emit("[MONITOR] Install progress monitor started");
            Emit($"[MONITOR] Searching {_installRoot}\\{{GUID}}\\ILog*.log and MSI*.log");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PollILog();
                    PollMsiLog();
                    PollEventViewer();
                }
                catch { /* don't crash the monitor */ }

                try { await Task.Delay(pollMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>Searches all {GUID} subfolders for the latest matching log file.</summary>
    private string? FindLatestLog(string pattern)
    {
        var candidates = new List<(string Path, DateTime Modified)>();

        if (Directory.Exists(_installRoot))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(_installRoot, "{*}"))
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                        candidates.Add((file, File.GetLastWriteTime(file)));
                }
                foreach (var file in Directory.GetFiles(_installRoot, pattern))
                    candidates.Add((file, File.GetLastWriteTime(file)));
            }
            catch { }
        }

        var altPaths = new[]
        {
            Path.GetTempPath(),
            @"C:\Windows\Temp",
            @"C:\ProgramData\ArchestrA\InstallLogs"
        };
        foreach (var alt in altPaths)
        {
            if (!Directory.Exists(alt)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(alt, pattern))
                    candidates.Add((file, File.GetLastWriteTime(file)));
            }
            catch { }
        }

        return candidates.OrderByDescending(c => c.Modified).FirstOrDefault().Path;
    }

    private void PollILog()
    {
        var logFile = FindLatestLog("ILog*.log");
        if (logFile == null) return;

        if (_ilogFile != null && logFile != _ilogFile)
        {
            Emit($"[MONITOR] Switched to newer ILog: {Path.GetFileName(logFile)}");
            _ilogLineCount = 0;
        }
        _ilogFile = logFile;

        try
        {
            var lines = File.ReadAllLines(logFile);
            if (lines.Length <= _ilogLineCount) return;

            for (int i = _ilogLineCount; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.Contains("Static Prerequisite Installation Completed"))
                    Emit("[PASS] Static prerequisites installed");
                else if (line.Contains("Dynamic Prerequisite Installation Completed"))
                    Emit("[PASS] Dynamic prerequisites installed");
                else if (line.Contains("WaitForFinish") && line.Contains("Configurator"))
                    Emit("[MSI ] Configurator launched \u2014 individual MSI installs starting");
                else if (line.Contains("AfterInstall preparing"))
                {
                    var m = Regex.Match(line, @"feature[=]""?(.+?)(?:""|[\s]/)");
                    if (m.Success)
                        Emit($"[CFG ] Post-install config: {m.Groups[1].Value.Trim()}");
                }
                else if (line.Contains("the return value is "))
                {
                    var m = Regex.Match(line, @"return value is (\d+)");
                    if (m.Success)
                    {
                        var code = int.Parse(m.Groups[1].Value);
                        Emit(code is 0 or 3010
                            ? $"[PASS] Setup return code: {code}"
                            : $"[FAIL] Setup return code: {code}");
                    }
                }
                else if (line.Contains("Error:") && !line.Contains("Error 87 getting VersionString"))
                {
                    var errIdx = line.IndexOf("Error:");
                    if (errIdx >= 0)
                        Emit($"[FAIL] ILog: {line[(errIdx + 6)..].Trim()}");
                }
            }
            _ilogLineCount = lines.Length;
        }
        catch (IOException) { /* file locked by installer */ }
    }

    private void PollMsiLog()
    {
        var logFile = FindLatestLog("MSI*.log");
        if (logFile == null) return;

        if (_msiLogFile != null && logFile != _msiLogFile)
        {
            Emit($"[MONITOR] Switched to newer MSI log: {Path.GetFileName(logFile)}");
            _msiLogPosition = 0;
        }
        _msiLogFile = logFile;

        try
        {
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= _msiLogPosition) return;
            fs.Seek(_msiLogPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.Unicode);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var nameMatch = Regex.Match(line, @"Property\(S\): ProductName = (.+)$");
                if (nameMatch.Success)
                {
                    _currentProduct = nameMatch.Groups[1].Value.Trim();
                    continue;
                }

                var finalMatch = Regex.Match(line, @"Action ended .+: InstallFinalize\. Return value (\d+)");
                if (finalMatch.Success)
                {
                    var retVal = int.Parse(finalMatch.Groups[1].Value);
                    var product = string.IsNullOrEmpty(_currentProduct) ? "Unknown" : _currentProduct;
                    _productIndex++;

                    if (retVal is 0 or 1)
                    {
                        _installed[product] = DateTime.Now;
                        Emit($"[{_productIndex:D2}] [PASS] {product}");
                    }
                    else
                    {
                        _failed[product] = $"InstallFinalize returned {retVal}";
                        Emit($"[{_productIndex:D2}] [FAIL] {product} (return {retVal})");
                    }
                    _currentProduct = "";
                }
            }
            _msiLogPosition = fs.Position;
        }
        catch (IOException) { /* file locked */ }
    }

    private void PollEventViewer()
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='MsiInstaller'] and TimeCreated[@SystemTime>='{_lastEventTime.ToUniversalTime():O}']]]");

            using var reader = new EventLogReader(query);
            EventRecord? record;
            while ((record = reader.ReadEvent()) != null)
            {
                var msg = record.FormatDescription() ?? "";
                var product = ExtractProduct(msg);

                switch (record.Id)
                {
                    case 11707 when !string.IsNullOrEmpty(product):
                        if (!_installed.ContainsKey(product))
                            Emit($"[EVTL] [PASS] {product}");
                        break;
                    case 11708 when !string.IsNullOrEmpty(product):
                        if (!_failed.ContainsKey(product))
                        {
                            _failed[product] = "EventLog 11708";
                            Emit($"[EVTL] [FAIL] {product}");
                        }
                        break;
                }

                if (record.TimeCreated.HasValue)
                    _lastEventTime = record.TimeCreated.Value.AddSeconds(1);
            }
        }
        catch (EventLogNotFoundException) { }
        catch { }
    }

    private void Emit(string message)
    {
        _channel.TryWrite(new ExecutionEvent
        {
            ExecutionId = _executionId,
            EventType = ExecutionEventType.EventProgress,
            OutputLine = message,
            Detail = message,
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
        });

        // Notify the heartbeat task of the latest install event
        OnProgress?.Invoke(message);
    }

    private static string ExtractProduct(string msg)
    {
        var m = Regex.Match(msg, @"Product:\s*(.+?)(?:\s*--)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\u2550\u2550 INSTALL SUMMARY: {_installed.Count} passed, {_failed.Count} failed \u2550\u2550");
        foreach (var p in _installed)
            sb.AppendLine($"  [PASS] {p.Key}");
        foreach (var p in _failed)
            sb.AppendLine($"  [FAIL] {p.Key} \u2014 {p.Value}");
        return sb.ToString();
    }

    public void Dispose() => _cts?.Cancel();
}
