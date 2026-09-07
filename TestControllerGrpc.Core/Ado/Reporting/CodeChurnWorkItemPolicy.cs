using Microsoft.Extensions.Configuration;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Which work items appear as rows in the Code Churn report, and what happens to the commits linked to the
/// ones that do not.
/// </summary>
/// <remarks>
/// THE RULE, stated once so nobody "simplifies" it back into a plain filter:
///
/// Excluding a type removes it as a REPORTED ROW. It does NOT remove the commits linked to it. Before
/// exclusion, every commit linked to an excluded work item is re-attributed to that work item's nearest
/// non-excluded ancestor. A commit is never dropped because its only link happened to be a Task.
///
/// A plain <c>Where(w =&gt; w.Type != "Task")</c> looks equivalent and is not: a commit whose only link is a
/// Task would vanish from the report entirely. Losing a change is the one failure this report cannot have.
/// </remarks>
public sealed class CodeChurnWorkItemPolicy
{
    public const string SectionName = "CodeChurn:WorkItemPolicy";

    /// <summary>ADO work item type names suppressed as rows. Compared case-insensitively.</summary>
    public IReadOnlyCollection<string> ExcludedTypes { get; init; } = ["Task"];

    public bool RollUpToFeature { get; init; } = true;

    public string OrphanBucketName { get; init; } = "Unassigned to Feature";

    public int MaxHierarchyDepth { get; init; } = 6;

    private HashSet<string>? _excluded;

    private HashSet<string> Excluded =>
        _excluded ??= new HashSet<string>(ExcludedTypes ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this work item should be suppressed as a row. Falls back to false when the raw ADO type is
    /// unknown: <see cref="RegressionWorkItemKind"/> folds Task, Feature and every unmapped type into
    /// <c>Other</c>, so guessing from it would suppress far more than asked.
    /// </summary>
    public bool IsExcluded(RegressionWorkItemRef workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return workItem.WorkItemType is { Length: > 0 } type && Excluded.Contains(type);
    }

    /// <summary>Throws with the offending value named, rather than silently clamping to a default.</summary>
    public void Validate()
    {
        if (MaxHierarchyDepth < 1)
            throw new InvalidOperationException(
                $"{SectionName}:MaxHierarchyDepth must be at least 1 (was {MaxHierarchyDepth}).");

        if (string.IsNullOrWhiteSpace(OrphanBucketName))
            throw new InvalidOperationException(
                $"{SectionName}:OrphanBucketName must not be empty — orphaned work items need a visible bucket.");

        if (ExcludedTypes is null)
            throw new InvalidOperationException($"{SectionName}:ExcludedTypes must not be null (use [] for none).");

        if (ExcludedTypes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{SectionName}:ExcludedTypes contains a blank entry.");
    }

    public static CodeChurnWorkItemPolicy Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var policy = new CodeChurnWorkItemPolicy();
        configuration.GetSection(SectionName).Bind(policy);
        policy.Validate();
        return policy;
    }
}
