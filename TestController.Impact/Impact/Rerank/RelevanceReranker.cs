using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Rerank;

/// <summary>Grades how relevant each candidate test case is to a change, 0-3 (P18).</summary>
public interface IRelevanceReranker
{
    /// <summary>Returns a graded relevance judgement per test case id. Fails open — never drops a candidate.</summary>
    Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct);
}

/// <summary>
/// No-op reranker (P18): grades every candidate 2 / confidence 0.5 / "rerank disabled". Used when LLM
/// rerank is turned off and throughout the early phases so the pipeline is always runnable.
/// </summary>
public sealed class PassThroughReranker : IRelevanceReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct)
    {
        var result = new Dictionary<int, RelevanceJudgement>();
        foreach (Scored<TestCaseCandidate> candidate in candidates)
        {
            result[candidate.Value.Item.Id] = new RelevanceJudgement(2, 0.5, "rerank disabled", []);
        }

        return Task.FromResult<IReadOnlyDictionary<int, RelevanceJudgement>>(result);
    }
}

/// <summary>
/// LLM graded reranker (P18). Grades are 0-3 (not binary) so the P22 budget knapsack can trade quality for
/// time via a threshold rather than a prompt change. Each judgement must cite the changed literal or symbol
/// that drove it; uncited judgements are downgraded to suppress "everything in a familiar subsystem looks
/// relevant". Borderline grades (1-2, low confidence) get adaptive self-consistency (two extra temp-0.3
/// passes, median grade). The reranker fails open everywhere: any parse failure, omission or transport error
/// yields grade 2 / confidence 0.5 so a model problem never drops a test case. Results are cached on
/// SHA256(fingerprint + id + revision) so re-analysing the same change costs no tokens.
/// </summary>
public sealed class LlmRelevanceReranker : IRelevanceReranker
{
    private const string SystemPrompt =
        "You grade how relevant each test case is to a code change on this rubric:\n" +
        "3 - the test case executes the changed code path directly\n" +
        "2 - the test case exercises a feature whose behaviour this change can alter\n" +
        "1 - the test case shares a subsystem but is unlikely to be affected\n" +
        "0 - unrelated\n" +
        "For every judgement cite the specific changed literal or symbol that drove it.\n" +
        "Respond with ONLY a JSON array, no prose, no markdown fences:\n" +
        "[{\"id\":<int>,\"grade\":<0-3>,\"confidence\":<0.0-1.0>,\"reason\":\"<=25 words\"," +
        "\"citedSignals\":[\"the changed literal or symbol\"]}]";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmClient _llm;
    private readonly ImpactMappingOptions.RerankOptions _options;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the reranker over the shared LLM client and rerank options.</summary>
    public LlmRelevanceReranker(ILlmClient llm, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _options = options.Value.Rerank;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, RelevanceJudgement>> RerankAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(candidates);

        var result = new Dictionary<int, RelevanceJudgement>();
        var toQuery = new List<Scored<TestCaseCandidate>>();
        foreach (Scored<TestCaseCandidate> candidate in candidates)
        {
            if (TryGetCached(CacheKey(change.Fingerprint, candidate.Value.Item), out RelevanceJudgement? cached))
            {
                result[candidate.Value.Item.Id] = cached;
            }
            else
            {
                toQuery.Add(candidate);
            }
        }

        if (toQuery.Count == 0)
        {
            return result;
        }

        Dictionary<int, RelevanceJudgement> graded = await GradeAllAsync(change, hyde, payload, toQuery, 0.0, ct).ConfigureAwait(false);
        ApplyCitedSignalDowngrade(graded);
        await ApplySelfConsistencyAsync(change, hyde, payload, toQuery, graded, ct).ConfigureAwait(false);

        foreach (Scored<TestCaseCandidate> candidate in toQuery)
        {
            TestCaseCandidate testCase = candidate.Value;
            if (!graded.TryGetValue(testCase.Item.Id, out RelevanceJudgement? judgement))
            {
                judgement = FailOpen();
                _logger.Warn("ImpactRerank", $"Test case {testCase.Item.Id} omitted by reranker; failing open at grade 2.");
            }

            result[testCase.Item.Id] = judgement;
            _cache[CacheKey(change.Fingerprint, testCase.Item)] = new CacheEntry(judgement, DateTimeOffset.UtcNow.Add(_options.CacheTtl));
        }

        return result;
    }

    private async Task<Dictionary<int, RelevanceJudgement>> GradeAllAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> candidates, double temperature, CancellationToken ct)
    {
        List<Scored<TestCaseCandidate>[]> batches = [.. candidates.Chunk(Math.Max(1, _options.BatchSize))];
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelism));

        IEnumerable<Task<Dictionary<int, RelevanceJudgement>>> tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GradeBatchWithRetryAsync(change, hyde, payload, batch, temperature, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var merged = new Dictionary<int, RelevanceJudgement>();
        foreach (Dictionary<int, RelevanceJudgement> batchResult in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            foreach ((int id, RelevanceJudgement judgement) in batchResult)
            {
                merged[id] = judgement;
            }
        }

        return merged;
    }

    private async Task<Dictionary<int, RelevanceJudgement>> GradeBatchWithRetryAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> batch, double temperature, CancellationToken ct)
    {
        string userContent = BuildUserContent(change, hyde, payload, batch);

        Dictionary<int, RelevanceJudgement>? parsed = await CallAndParseAsync(userContent, temperature, stricter: false, ct).ConfigureAwait(false);
        parsed ??= await CallAndParseAsync(userContent, temperature, stricter: true, ct).ConfigureAwait(false);

        if (parsed is null)
        {
            _logger.Warn("ImpactRerank", $"Reranker returned unparseable output for a batch of {batch.Count}; failing open.");
            return [];
        }

        return parsed;
    }

    private async Task<Dictionary<int, RelevanceJudgement>?> CallAndParseAsync(
        string userContent, double temperature, bool stricter, CancellationToken ct)
    {
        var messages = new List<LlmMessage>
        {
            new("system", SystemPrompt),
            new("user", stricter ? userContent + "\n\nReturn ONLY the JSON array." : userContent),
        };

        string? response = await _llm
            .CompleteAsync(new LlmRequest(_options.LlmModel, messages, MaxOutputTokens: 1200, Temperature: temperature, JsonOutput: true), ct)
            .ConfigureAwait(false);

        return response is null ? null : Parse(response);
    }

    private static Dictionary<int, RelevanceJudgement>? Parse(string response)
    {
        try
        {
            List<RerankEntry>? entries = JsonSerializer.Deserialize<List<RerankEntry>>(StripFences(response), JsonOptions);
            if (entries is null)
            {
                return null;
            }

            var result = new Dictionary<int, RelevanceJudgement>();
            foreach (RerankEntry entry in entries)
            {
                int grade = Math.Clamp(entry.Grade, 0, 3);
                double confidence = Math.Clamp(entry.Confidence, 0.0, 1.0);
                result[entry.Id] = new RelevanceJudgement(
                    grade, confidence, entry.Reason ?? string.Empty,
                    (entry.CitedSignals ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList());
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ApplyCitedSignalDowngrade(Dictionary<int, RelevanceJudgement> graded)
    {
        int downgrade = (int)Math.Round(_options.DowngradeWhenNoCitedSignals);
        if (downgrade <= 0)
        {
            return;
        }

        foreach (int id in graded.Keys.ToList())
        {
            RelevanceJudgement judgement = graded[id];
            if (judgement.CitedSignals.Count == 0)
            {
                graded[id] = judgement with { Grade = Math.Max(0, judgement.Grade - downgrade) };
            }
        }
    }

    private async Task ApplySelfConsistencyAsync(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload,
        IReadOnlyList<Scored<TestCaseCandidate>> queried, Dictionary<int, RelevanceJudgement> graded, CancellationToken ct)
    {
        List<Scored<TestCaseCandidate>> borderline = queried
            .Where(c => graded.TryGetValue(c.Value.Item.Id, out RelevanceJudgement? j)
                && j.Grade is 1 or 2
                && j.Confidence < _options.SelfConsistencyConfidenceThreshold)
            .ToList();

        if (borderline.Count == 0)
        {
            return;
        }

        Dictionary<int, RelevanceJudgement> pass2 = await GradeAllAsync(change, hyde, payload, borderline, 0.3, ct).ConfigureAwait(false);
        Dictionary<int, RelevanceJudgement> pass3 = await GradeAllAsync(change, hyde, payload, borderline, 0.3, ct).ConfigureAwait(false);

        foreach (Scored<TestCaseCandidate> candidate in borderline)
        {
            int id = candidate.Value.Item.Id;
            RelevanceJudgement initial = graded[id];
            int[] grades =
            [
                initial.Grade,
                pass2.TryGetValue(id, out RelevanceJudgement? j2) ? j2.Grade : initial.Grade,
                pass3.TryGetValue(id, out RelevanceJudgement? j3) ? j3.Grade : initial.Grade,
            ];
            Array.Sort(grades);
            graded[id] = initial with { Grade = grades[1] }; // median of three
        }
    }

    private string BuildUserContent(
        ChangeDocument change, HydeQuery hyde, ChangePayload payload, IReadOnlyList<Scored<TestCaseCandidate>> batch)
    {
        var builder = new StringBuilder();
        builder.Append("Change summary:\n").Append(hyde.ChangeSummary).Append("\n\n");

        IReadOnlyList<string> hunks = SelectHunks(change, payload);
        if (hunks.Count > 0)
        {
            builder.Append("Most relevant changed hunks:\n");
            foreach (string hunk in hunks)
            {
                builder.Append(hunk).Append("\n---\n");
            }

            builder.Append('\n');
        }

        builder.Append("Candidate test cases:\n");
        foreach (Scored<TestCaseCandidate> candidate in batch)
        {
            TestCaseCandidate testCase = candidate.Value;
            builder
                .Append("id=").Append(testCase.Item.Id)
                .Append(" | title=").Append(testCase.Item.Title)
                .Append(" | parentFeatureId=").Append(testCase.ParentFeatureId?.ToString() ?? "none")
                .Append(" | description=").Append(Truncate(testCase.Description, 400))
                .Append(" | steps=").Append(Truncate(testCase.StepsText, 400))
                .Append('\n');
        }

        return builder.ToString();
    }

    private IReadOnlyList<string> SelectHunks(ChangeDocument change, ChangePayload payload)
    {
        var signals = change.ChangedLiterals.Concat(change.PublicApiChanges).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (payload.Diffs ?? [])
            .SelectMany(d => d.Hunks ?? [])
            .Select(h => (Text: h.Text, Score: signals.Count(s => h.Text.Contains(s, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(1, _options.MaxDiffHunksToLlm))
            .Select(x => CapLines(x.Text, Math.Max(1, _options.MaxHunkLines)))
            .ToList();
    }

    private static string CapLines(string text, int maxLines)
        => string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Take(maxLines));

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static string StripFences(string response)
    {
        string trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static RelevanceJudgement FailOpen() => new(2, 0.5, "rerank unavailable", []);

    private bool TryGetCached(string key, out RelevanceJudgement judgement)
    {
        if (_cache.TryGetValue(key, out CacheEntry? entry) && entry.Expiry > DateTimeOffset.UtcNow)
        {
            judgement = entry.Judgement;
            return true;
        }

        judgement = null!;
        return false;
    }

    private static string CacheKey(string fingerprint, AdoWorkItemRef testCase)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{fingerprint}:{testCase.Id}:{testCase.Revision}"));
        return Convert.ToHexString(hash);
    }

    private sealed record CacheEntry(RelevanceJudgement Judgement, DateTimeOffset Expiry);

    private sealed record RerankEntry(int Id, int Grade, double Confidence, string? Reason, List<string>? CitedSignals);
}
