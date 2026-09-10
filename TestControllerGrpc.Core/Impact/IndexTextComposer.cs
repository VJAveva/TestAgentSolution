namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// The single definition of what text represents a work item in the retrieval index. Production indexing and
/// the test fixtures must both use this: when they drift, the fixture measures a corpus that does not exist
/// and a retrieval change can look like a no-op.
/// </summary>
public static class IndexTextComposer
{
    /// <summary>
    /// Title, description, steps and tags. Description is included because test case titles are frequently a
    /// bare requirement id ("FR 12345") carrying no matchable vocabulary, leaving description as the only
    /// functional prose on the item.
    /// </summary>
    public static string ForTestCase(TestCaseCandidate testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        string? tags = testCase.Tags is { Count: > 0 } ? string.Join(' ', testCase.Tags) : null;
        return Join(testCase.Item.Title, testCase.Description, testCase.StepsText, tags);
    }

    /// <summary>Title and description.</summary>
    public static string ForFeature(FeatureCandidate feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return Join(feature.Item.Title, feature.Description);
    }

    private static string Join(params string?[] parts)
        => string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
