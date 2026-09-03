using System.Text;
using System.Text.Json;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Builds compact per-file diffs for a commit from Azure DevOps: lists changed paths, fetches file
/// content at the commit and its first parent, and renders a +/- patch via DiffPlex. Output is
/// capped (files + bytes) and passed through <see cref="SecurityRedactor"/> so secrets in changed
/// config never reach the model. Binary/oversized files are summarized as placeholders.
/// </summary>
public sealed class AdoChangeDiffSource : IChangeDiffSource
{
    private static readonly InlineDiffBuilder DiffBuilder = new(new Differ());

    private readonly AdoClient _client;
    private readonly IGitQueries _git;
    private readonly LlmOptions _options;
    private readonly IAppLogger _logger;

    public AdoChangeDiffSource(AdoClient client, IGitQueries git, IOptions<LlmOptions> options, IAppLogger logger)
    {
        _client = client;
        _git = git;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CommitDiff> GetCommitDiffAsync(string project, string repositoryId, string commitId, CancellationToken ct)
    {
        var parentId = await GetFirstParentAsync(project, repositoryId, commitId, ct);
        var changes = await _git.GetCommitChangesAsync(project, repositoryId, commitId, ct);

        var files = new List<FileDiff>();
        foreach (var (path, changeType) in changes.Take(_options.MaxFilesPerCommit))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(await BuildFilePatchAsync(project, repositoryId, commitId, parentId, path, changeType, ct));
        }
        return new CommitDiff(commitId, files);
    }

    private async Task<string?> GetFirstParentAsync(string project, string repositoryId, string commitId, CancellationToken ct)
    {
        var path = _client.ProjectApiPath(project, $"git/repositories/{repositoryId}/commits/{commitId}?api-version=7.1");
        var json = await _client.GetStringAsync(path, ct);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parents", out var parents) &&
                parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0)
                return parents[0].GetString();
        }
        catch (JsonException ex)
        {
            _logger.Warn("Llm", $"Could not parse parents for commit {Short(commitId)}: {ex.Message}");
        }
        return null;
    }

    private async Task<FileDiff> BuildFilePatchAsync(
        string project, string repositoryId, string commitId, string? parentId, string path, string changeType, CancellationToken ct)
    {
        var target = await GetItemTextAsync(project, repositoryId, commitId, path, ct);
        var baseline = parentId is null ? "" : await GetItemTextAsync(project, repositoryId, parentId, path, ct);

        if (LooksBinary(target) || LooksBinary(baseline))
            return new FileDiff(path, changeType, "(binary or non-text change — not diffed)", Truncated: false);

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');

        var truncated = false;
        foreach (var line in DiffBuilder.BuildDiffModel(baseline, target).Lines)
        {
            var marker = line.Type switch
            {
                ChangeType.Inserted => "+",
                ChangeType.Deleted => "-",
                _ => null,
            };
            if (marker is null) continue; // only emit changed lines to stay compact
            sb.Append(marker).Append(line.Text).Append('\n');
            if (sb.Length >= _options.MaxDiffBytesPerFile) { truncated = true; break; }
        }

        var patch = SecurityRedactor.Redact(sb.ToString()) ?? "";
        return new FileDiff(path, changeType, patch, truncated);
    }

    private Task<string> GetItemTextAsync(string project, string repositoryId, string version, string path, CancellationToken ct)
    {
        var api = _client.ProjectApiPath(project,
            $"git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}" +
            $"&versionDescriptor.versionType=commit&versionDescriptor.version={version}" +
            "&includeContent=true&$format=text&api-version=7.1");
        return _client.GetStringAsync(api, ct); // "" on 404 (added/deleted side)
    }

    private static bool LooksBinary(string s) => s.IndexOf('\0') >= 0;

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
}
