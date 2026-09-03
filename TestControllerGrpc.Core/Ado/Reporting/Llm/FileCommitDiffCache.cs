using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Two-tier cache for immutable commit diffs: an in-process dictionary backed by an on-disk JSON file
/// per {repo}:{commit}. Diffs never change, so entries are written once and never invalidated. All disk
/// I/O is best-effort and never throws into the caller.
/// </summary>
public sealed class FileCommitDiffCache : ICommitDiffCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, CommitDiff> _memory = new();
    private readonly string _dir;
    private readonly IAppLogger _logger;

    public FileCommitDiffCache(IOptions<LlmOptions> options, IAppLogger logger)
    {
        _logger = logger;
        _dir = string.IsNullOrWhiteSpace(options.Value.CacheDirectory)
            ? Path.Combine(AppLogger.DefaultLogDirectory, "llm-cache")
            : options.Value.CacheDirectory!;
        try { Directory.CreateDirectory(_dir); } catch { /* best-effort */ }
    }

    public bool TryGet(string repositoryId, string commitId, out CommitDiff? diff)
    {
        var key = Key(repositoryId, commitId);
        if (_memory.TryGetValue(key, out var cached)) { diff = cached; return true; }

        try
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<CommitDiff>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    _memory[key] = loaded;
                    diff = loaded;
                    return true;
                }
            }
        }
        catch (Exception ex) { _logger.Warn("Llm", $"Diff cache read failed: {ex.Message}"); }

        diff = null;
        return false;
    }

    public void Set(string repositoryId, string commitId, CommitDiff diff)
    {
        var key = Key(repositoryId, commitId);
        _memory[key] = diff;
        try { File.WriteAllText(PathFor(key), JsonSerializer.Serialize(diff, JsonOptions)); }
        catch (Exception ex) { _logger.Warn("Llm", $"Diff cache write failed: {ex.Message}"); }
    }

    private static string Key(string repositoryId, string commitId) => $"{repositoryId}:{commitId}";

    private string PathFor(string key)
    {
        // Stable, filesystem-safe file name for an arbitrary repo:commit key.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(_dir, hash + ".json");
    }
}
