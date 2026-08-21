using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>The consumed-component "Name : Version" manifest captured for one build.</summary>
public sealed record BuildManifest(int BuildId, IReadOnlyDictionary<string, string> ComponentVersions);

/// <summary>
/// The result of diffing two build manifests — components whose consumed version moved. This is the
/// modern replacement for the legacy <c>CompareBuildsForDiff</c> log-scraping in Program.cs.
/// </summary>
public sealed record ManifestDiff(IReadOnlyDictionary<string, string> Changed, IReadOnlyDictionary<string, string> Unchanged);

/// <summary>Supplies the consumed-component version manifest for a build.</summary>
public interface IBuildManifestSource
{
    Task<BuildManifest> GetManifestAsync(string project, int buildId, int logId, CancellationToken ct);
}

/// <summary>
/// Reads the manifest from a build-timeline log (the legacy source: <c>GetLogForSP</c> downloaded a log
/// whose text lists "Name : Version" pairs). Fetches GET build/builds/{id}/logs/{logId} and parses.
/// </summary>
public sealed class BuildLogManifestSource : IBuildManifestSource
{
    private static int _scanned; // one-time full-log scan guard (diagnostic)

    private readonly AdoClient _client;
    private readonly IAppLogger _logger;

    public BuildLogManifestSource(AdoClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<BuildManifest> GetManifestAsync(string project, int buildId, int logId, CancellationToken ct)
    {
        if (buildId <= 0)
            return new BuildManifest(buildId, new Dictionary<string, string>());

        var path = _client.ProjectApiPath(project, $"build/builds/{buildId}/logs/{logId}?api-version=7.1");
        var text = await _client.GetStringAsync(path, ct);
        var versions = ManifestParser.Parse(text);
        _logger.Info("Ado", $"Manifest for build {buildId} (project '{project}', log {logId}) parsed {versions.Count} component versions.");

        // When the configured log yields no manifest, scan every log ONCE to find which one holds the
        // consumed-component versions (the manifest-log id is a runtime assumption to be pinned down).
        if (versions.Count == 0 && Interlocked.Exchange(ref _scanned, 1) == 0)
            await ScanAllLogsAsync(project, buildId, ct);

        return new BuildManifest(buildId, versions);
    }

    private async Task ScanAllLogsAsync(string project, int buildId, CancellationToken ct)
    {
        _logger.Info("Ado", $"MANIFEST-SCAN starting one-time full-log scan for build {buildId} (project '{project}')…");

        // The consumed-component CHAIN (Build Tracer model) lives in the pipeline run's resources
        // (resources.pipelines), not in logs. Probe it: build -> definition id -> pipeline run resources.
        try
        {
            var buildJson = await _client.GetStringAsync(
                _client.ProjectApiPath(project, $"build/builds/{buildId}?api-version=7.1"), ct);
            using var bdoc = System.Text.Json.JsonDocument.Parse(buildJson);
            var defId = bdoc.RootElement.GetProperty("definition").GetProperty("id").GetInt32();
            var runJson = await _client.GetStringAsync(
                _client.ProjectApiPath(project, $"pipelines/{defId}/runs/{buildId}?api-version=7.1"), ct);
            _logger.Info("Ado", $"PIPELINE-RUN build {buildId} (def {defId}) resources: {Trunc(runJson, 3000)}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"PIPELINE-RUN probe failed for build {buildId}: {ex.Message}");
        }

        // Prove ADO access + reveal the real definition list (answers "no sign of accessing ADO").
        try
        {
            var defsJson = await _client.GetStringAsync(
                _client.ProjectApiPath(project, "build/definitions?api-version=7.1"), ct);
            _logger.Info("Ado", $"MANIFEST-SCAN build definitions in '{project}': {Trunc(defsJson, 1800)}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"MANIFEST-SCAN could not list definitions: {ex.Message}");
        }

        // Also dump the build's artifacts — a consumed-component BOM/manifest is often published as an
        // artifact rather than printed to a log. This one probe tells us which source actually holds it.
        try
        {
            var artifactsJson = await _client.GetStringAsync(
                _client.ProjectApiPath(project, $"build/builds/{buildId}/artifacts?api-version=7.1"), ct);
            _logger.Info("Ado", $"MANIFEST-SCAN artifacts for build {buildId}: {Trunc(artifactsJson, 1500)}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"MANIFEST-SCAN could not list artifacts: {ex.Message}");
        }

        List<int> logIds;
        try
        {
            var listJson = await _client.GetStringAsync(
                _client.ProjectApiPath(project, $"build/builds/{buildId}/logs?api-version=7.1"), ct);
            using var doc = System.Text.Json.JsonDocument.Parse(listJson);
            logIds = doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(e => e.GetProperty("id").GetInt32())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"MANIFEST-SCAN could not list logs: {ex.Message}");
            return;
        }

        foreach (var id in logIds)
        {
            try
            {
                var text = await _client.GetStringAsync(
                    _client.ProjectApiPath(project, $"build/builds/{buildId}/logs/{id}?api-version=7.1"), ct);
                var lines = text.Split('\n').Select(l => AnsiStrip(l).TrimEnd()).ToArray();

                // Find the first line that looks like a "Version" column/label and dump a window around it
                // so the real manifest table format (header + ---- + rows) becomes visible.
                var anchor = Array.FindIndex(lines, l => l.Contains("Version", StringComparison.OrdinalIgnoreCase));
                var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                _logger.Info("Ado", $"MANIFEST-SCAN log {id}: len={text.Length}, versionAnchor={anchor}, firstLine=\"{Trunc(first, 120)}\"");

                if (anchor >= 0)
                {
                    var end = Math.Min(lines.Length, anchor + 14);
                    for (var i = anchor; i < end; i++)
                    {
                        var l = lines[i].Trim();
                        if (l.Length > 0)
                            _logger.Info("Ado", $"  SCAN-LINE[{id}:{i}]| {Trunc(l, 220)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("Ado", $"MANIFEST-SCAN log {id} failed: {ex.Message}");
            }
        }
        _logger.Info("Ado", $"MANIFEST-SCAN complete for build {buildId}.");
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    private static string AnsiStrip(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
}

/// <summary>Parses "Name : Version" component pairs out of build-log text (both ANSI and padded formats).</summary>
public static class ManifestParser
{
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);
    private static readonly Regex NameLine = new(@"^Name\s*:\s*(?<v>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionLine = new(@"^Version\s*:\s*(?<v>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyDictionary<string, string> Parse(string logText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(logText))
            return result;

        string? pendingName = null;
        foreach (var rawLine in logText.Split('\n'))
        {
            var line = AnsiEscape.Replace(rawLine, "").Trim();
            if (line.Length == 0)
                continue;

            var nameMatch = NameLine.Match(line);
            if (nameMatch.Success)
            {
                pendingName = nameMatch.Groups["v"].Value.Trim();
                continue;
            }

            var versionMatch = VersionLine.Match(line);
            if (versionMatch.Success && pendingName is not null)
            {
                if (!result.ContainsKey(pendingName))
                    result[pendingName] = versionMatch.Groups["v"].Value.Trim();
                pendingName = null;
            }
        }
        return result;
    }
}

/// <summary>Pure diff of two manifests: a component is "changed" when its version differs between builds.</summary>
public static class BuildManifestDiffer
{
    public static ManifestDiff Diff(BuildManifest current, BuildManifest old)
    {
        var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unchanged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, version) in current.ComponentVersions)
        {
            if (old.ComponentVersions.TryGetValue(name, out var oldVersion) && oldVersion == version)
                unchanged[name] = version;
            else
                changed[name] = version; // new component or moved version
        }
        return new ManifestDiff(changed, unchanged);
    }
}
