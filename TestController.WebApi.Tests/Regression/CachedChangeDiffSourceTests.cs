using TestControllerGrpc.Ado.Reporting.Llm;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Unit tests for the commit-diff caching decorator. Immutable {repo}:{commit} diffs must be fetched
/// from the inner source at most once. Uses dependency-free fakes (no network, no disk).
/// </summary>
public class CachedChangeDiffSourceTests
{
    [Fact]
    public async Task GetCommitDiffAsync_Should_NotRefetch_When_CommitAlreadyCached()
    {
        var inner = new CountingDiffSource();
        var sut = new CachedChangeDiffSource(inner, new InMemoryCommitDiffCache());

        await sut.GetCommitDiffAsync("proj", "repo", "sha1", default);
        var second = await sut.GetCommitDiffAsync("proj", "repo", "sha1", default);

        Assert.Equal(1, inner.Calls);
        Assert.Equal("sha1", second.CommitId);
    }

    [Fact]
    public async Task GetCommitDiffAsync_Should_Refetch_When_DifferentCommit()
    {
        var inner = new CountingDiffSource();
        var sut = new CachedChangeDiffSource(inner, new InMemoryCommitDiffCache());

        await sut.GetCommitDiffAsync("proj", "repo", "sha1", default);
        await sut.GetCommitDiffAsync("proj", "repo", "sha2", default);

        Assert.Equal(2, inner.Calls);
    }

    private sealed class CountingDiffSource : IChangeDiffSource
    {
        public int Calls;

        public Task<CommitDiff> GetCommitDiffAsync(string project, string repositoryId, string commitId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CommitDiff(commitId, []));
        }
    }

    private sealed class InMemoryCommitDiffCache : ICommitDiffCache
    {
        private readonly Dictionary<string, CommitDiff> _store = new();

        public bool TryGet(string repositoryId, string commitId, out CommitDiff? diff)
        {
            var ok = _store.TryGetValue($"{repositoryId}:{commitId}", out var v);
            diff = v;
            return ok;
        }

        public void Set(string repositoryId, string commitId, CommitDiff diff) =>
            _store[$"{repositoryId}:{commitId}"] = diff;
    }
}
