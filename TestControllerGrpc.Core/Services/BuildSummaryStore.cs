using System.IO;
using System.Text.Json;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Persists a small JSON list of per-build summaries (build number, timestamp,
/// pass rate) so the report card can render the last-N-builds trend strip and
/// the "vs last build" delta. One tiny file under the ReportResults root.
/// </summary>
public sealed class BuildSummaryStore
{
    private readonly BuildReportCardConfig _config;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public BuildSummaryStore(BuildReportCardConfig config, IAppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    private string StorePath =>
        string.IsNullOrWhiteSpace(_config.SummaryStorePath)
            ? Path.Combine(_config.ReportResultsRoot, ".reportcard", "summaries.json")
            : _config.SummaryStorePath;

    public List<BuildSummaryRecord> Load()
    {
        lock (_gate)
        {
            try
            {
                var path = StorePath;
                if (!File.Exists(path)) return [];
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<BuildSummaryRecord>>(json);
                return list ?? [];
            }
            catch (Exception ex)
            {
                _logger.Warn("BuildReportCard", $"Failed to read summary store: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>
    /// Records (or updates) the summary for a build and returns the full,
    /// chronologically ordered history (oldest first).
    /// </summary>
    public List<BuildSummaryRecord> Save(BuildSummaryRecord record)
    {
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(r =>
                string.Equals(r.BuildNumber, record.BuildNumber, StringComparison.OrdinalIgnoreCase));
            list.Add(record);
            list = list.OrderBy(r => r.GeneratedUtc).ToList();

            try
            {
                var path = StorePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
            }
            catch (Exception ex)
            {
                _logger.Warn("BuildReportCard", $"Failed to write summary store: {ex.Message}");
            }

            return list;
        }
    }
}
