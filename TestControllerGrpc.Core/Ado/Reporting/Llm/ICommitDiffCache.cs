namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Cache for immutable per-commit diffs, keyed by repository + commit id. A commit's diff never
/// changes, so entries are written once and never invalidated.
/// </summary>
public interface ICommitDiffCache
{
    bool TryGet(string repositoryId, string commitId, out CommitDiff? diff);
    void Set(string repositoryId, string commitId, CommitDiff diff);
}
