namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>A single changed file's compact, unified-style diff (only +/- lines).</summary>
public sealed record FileDiff(string Path, string ChangeType, string Patch, bool Truncated);

/// <summary>All changed files for one commit, as diffs ready to feed an LLM.</summary>
public sealed record CommitDiff(string CommitId, IReadOnlyList<FileDiff> Files);

/// <summary>
/// Produces compact, redacted code diffs for a commit so an LLM can summarize the actual change
/// (not just file paths). Implemented by <see cref="AdoChangeDiffSource"/> against Azure DevOps.
/// </summary>
public interface IChangeDiffSource
{
    Task<CommitDiff> GetCommitDiffAsync(string project, string repositoryId, string commitId, CancellationToken ct);
}
