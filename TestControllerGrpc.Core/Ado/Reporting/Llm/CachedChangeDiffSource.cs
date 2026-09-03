namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Caching decorator over an inner <see cref="IChangeDiffSource"/> (the ADO one). Immutable commit
/// diffs are served from <see cref="ICommitDiffCache"/>, so repeated summaries don't re-fetch file
/// content from Azure DevOps for commits already seen.
/// </summary>
public sealed class CachedChangeDiffSource : IChangeDiffSource
{
    private readonly IChangeDiffSource _inner;
    private readonly ICommitDiffCache _cache;

    public CachedChangeDiffSource(IChangeDiffSource inner, ICommitDiffCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<CommitDiff> GetCommitDiffAsync(string project, string repositoryId, string commitId, CancellationToken ct)
    {
        if (_cache.TryGet(repositoryId, commitId, out var cached) && cached is not null)
            return cached;

        var diff = await _inner.GetCommitDiffAsync(project, repositoryId, commitId, ct);
        _cache.Set(repositoryId, commitId, diff);
        return diff;
    }
}
