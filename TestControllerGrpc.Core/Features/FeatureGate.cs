using System.Text.Json;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Features;

/// <summary>Toggleable feature keys. Pipelines, Agents, Library, Pre-flight and Admin are core and never listed.</summary>
public static class FeatureKeys
{
    public const string Results = "Results";
    public const string FirReport = "FirReport";
    public const string CodeChurn = "CodeChurn";
    public const string ImpactMapping = "ImpactMapping";
    public const string PipelineBuilder = "PipelineBuilder";
    public const string GlobalVariables = "GlobalVariables";
    public const string Reports = "Reports";

    public static readonly IReadOnlyList<string> All =
        [Results, FirReport, CodeChurn, ImpactMapping, PipelineBuilder, GlobalVariables, Reports];

    /// <summary>A feature is off whenever the feature it depends on is off.</summary>
    public static readonly IReadOnlyDictionary<string, string> DependsOn =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FirReport] = Results,
            [ImpactMapping] = CodeChurn,
        };

    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Results] = "Build results dashboard over MSTest TRX files.",
            [FirReport] = "First Information Report for failed tests (rule packs, test-data workbook).",
            [CodeChurn] = "Code Churn: Azure DevOps changes per component and branch.",
            [ImpactMapping] = "Impacted test cases for each changed component (impact index).",
            [PipelineBuilder] = "Pipeline Builder wizard that generates new pipelines.",
            [GlobalVariables] = "Global Variables editor (GlobalVariables.json).",
            [Reports] = "Build Report Card and the consolidated run email.",
        };

    public static readonly IReadOnlySet<string> Core =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pipelines", "Agents", "Library", "Preflight", "Pre-flight", "Admin" };
}

/// <summary>Resolved state of one feature for display and the API.</summary>
public sealed record FeatureState(
    string Key, bool Enabled, bool ConfiguredOn, string? OffBecause, string Description, string? DependsOn);

public interface IFeatureGate
{
    bool IsEnabled(string key);
    IReadOnlyList<FeatureState> All();
    string TeamName { get; }
    string ConfigPath { get; }

    /// <summary>Why the last reload was rejected; null when the file loaded (or is absent).</summary>
    string? LastError { get; }

    event Action? Changed;
}

/// <summary>
/// Per-controller feature toggles from Features.json. A missing file or key means ON, so an install
/// without the file behaves exactly as before. A bad file keeps the last good copy.
/// </summary>
public sealed class FeatureGate : IFeatureGate, IDisposable
{
    private const string LogCategory = "Features";
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IAppLogger? _logger;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private Dictionary<string, bool> _configured = new(StringComparer.OrdinalIgnoreCase);
    private string _teamName = "";

    public FeatureGate(string configPath, IAppLogger? logger = null, bool watch = true)
    {
        ConfigPath = configPath;
        _logger = logger;
        Reload();
        if (watch) StartWatching();
    }

    public string ConfigPath { get; }
    public string TeamName { get { lock (_gate) return _teamName; } }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public bool IsEnabled(string key)
    {
        lock (_gate) return Resolve(key, _configured).Enabled;
    }

    public IReadOnlyList<FeatureState> All()
    {
        lock (_gate) return FeatureKeys.All.Select(k => Resolve(k, _configured)).ToList();
    }

    /// <summary>Re-reads the file. Public so tests need not race a FileSystemWatcher.</summary>
    public void Reload()
    {
        if (!File.Exists(ConfigPath))
        {
            Apply("", new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), null);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath), ReadOptions);
            var root = doc.RootElement;
            var team = root.TryGetProperty("TeamName", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            var configured = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Features", out var features) && features.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in features.EnumerateObject())
                {
                    if (FeatureKeys.Core.Contains(p.Name))
                    {
                        _logger?.Warn(LogCategory, $"'{p.Name}' is a core feature and cannot be turned off; ignored.");
                        continue;
                    }
                    if (!FeatureKeys.All.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger?.Warn(LogCategory, $"Unknown feature key '{p.Name}' in {ConfigPath}; ignored.");
                        continue;
                    }
                    if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        _logger?.Warn(LogCategory, $"Feature '{p.Name}' must be true or false; treated as on.");
                        continue;
                    }
                    configured[p.Name] = p.Value.GetBoolean();
                }
            }

            Apply(team, configured, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var reason = $"{ConfigPath} could not be loaded ({ex.Message}); keeping the last good feature set.";
            LastError = reason;
            _logger?.Warn(LogCategory, reason);
        }
    }

    private void Apply(string team, Dictionary<string, bool> configured, string? error)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_teamName, team, StringComparison.Ordinal)
                || _configured.Count != configured.Count
                || _configured.Any(kv => !configured.TryGetValue(kv.Key, out var v) || v != kv.Value);
            _teamName = team;
            _configured = configured;
        }
        LastError = error;

        if (changed)
        {
            var off = FeatureKeys.All.Where(k => !IsEnabled(k)).ToList();
            _logger?.Info(LogCategory, off.Count == 0
                ? "All features enabled."
                : $"Features turned off on this controller: {string.Join(", ", off)}.");
            Changed?.Invoke();
        }
    }

    private static FeatureState Resolve(string key, IReadOnlyDictionary<string, bool> configured)
    {
        var configuredOn = !configured.TryGetValue(key, out var value) || value;
        FeatureKeys.DependsOn.TryGetValue(key, out var parent);
        FeatureKeys.Descriptions.TryGetValue(key, out var description);

        string? offBecause = null;
        var enabled = configuredOn;
        if (configuredOn && parent is not null && !Resolve(parent, configured).Enabled)
        {
            enabled = false;
            offBecause = $"off because {parent} is off";
        }

        return new FeatureState(key, enabled, configuredOn, offBecause, description ?? "", parent);
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(ConfigPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory, $"Cannot watch {ConfigPath} for changes: {ex.Message}");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(300, cts.Token).ContinueWith(_ => Reload(), cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Cancel();
    }
}
