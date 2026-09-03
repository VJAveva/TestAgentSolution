using System.Text.RegularExpressions;
using TestControllerGrpc.Core.Impact.Rerank;

namespace TestControllerGrpc.Core.Impact.Testing;

/// <summary>
/// Deterministic rule-based reranker for tests and fixtures (P26). Grades each candidate by how many changed
/// literals/symbols appear in its title and steps — no LLM, fully reproducible — so pipeline tests can assert
/// on grades without a model.
/// </summary>
public sealed partial class FakeRelevanceReranker : IRelevanceReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct)
    {
        var signals = change.ChangedLiterals
            .Concat(change.ChangedSymbols)
            .SelectMany(Tokens)
            .ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<int, RelevanceJudgement>();
        foreach (Scored<TestCaseCandidate> candidate in candidates)
        {
            TestCaseCandidate testCase = candidate.Value;
            var testCaseTokens = Tokens($"{testCase.Item.Title} {testCase.StepsText}").ToHashSet(StringComparer.Ordinal);
            int overlap = signals.Count(testCaseTokens.Contains);
            int grade = overlap >= 3 ? 3 : overlap;
            result[testCase.Item.Id] = new RelevanceJudgement(
                grade, 0.8, $"token overlap {overlap}", overlap > 0 ? ["token overlap"] : []);
        }

        return Task.FromResult<IReadOnlyDictionary<int, RelevanceJudgement>>(result);
    }

    private static IEnumerable<string> Tokens(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : WordSplitter().Split(text.ToLowerInvariant()).Where(t => t.Length >= 3);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplitter();
}
