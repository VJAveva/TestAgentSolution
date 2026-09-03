using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Query;

/// <summary>Generates a HyDE (hypothetical document embeddings) query from a change (P12).</summary>
public interface IHydeQueryGenerator
{
    /// <summary>
    /// Writes what a QA engineer's test case for this change would plausibly look like, so retrieval can
    /// search a test-case corpus with test-shaped text instead of code identifiers. Never throws: on any
    /// LLM or parse failure it falls back to a deterministic query built from the change document.
    /// </summary>
    Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct);
}

/// <summary>
/// Closes the code-to-test vocabulary gap (P12). A single temperature-0 LLM call turns the change into
/// functional, user-facing test language; the result is embedded and searched downstream, which moves
/// recall more than any ranking weight. Reuses the shared <see cref="ILlmClient"/> so the Azure resource,
/// key handling and retry live in one place. Degrades to an offline query when HyDE is disabled, no chat
/// model is configured, or the model output cannot be parsed after one stricter retry.
/// </summary>
public sealed class HydeQueryGenerator : IHydeQueryGenerator
{
    private const string SystemPrompt =
        "You translate code changes into the language a QA engineer uses when writing test cases for an " +
        "industrial automation platform. You describe only behaviour that the given change plausibly affects. " +
        "You never invent product features that are not implied by the change. You never mention class names, " +
        "file names or code identifiers in the synthetic test text — write in functional user-facing language.";

    private const string JsonInstruction =
        "Respond with ONLY a JSON object — no prose, no markdown fences — of exactly this shape: " +
        "{\"changeSummary\":\"2-3 sentences\",\"syntheticTestTitles\":[\"5 to 8 plausible test case titles\"]," +
        "\"syntheticTestBody\":\"one paragraph of plausible test steps\"," +
        "\"expandedTerms\":[\"functional synonyms of the code terms\"]}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmClient _llm;
    private readonly ImpactMappingOptions.RerankOptions _options;
    private readonly IAppLogger _logger;

    /// <summary>Creates the generator over the shared LLM client and impact rerank options.</summary>
    public HydeQueryGenerator(ILlmClient llm, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _options = options.Value.Rerank;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (!_options.EnableHyde || string.IsNullOrWhiteSpace(_options.LlmModel))
        {
            return Fallback(doc);
        }

        string userContent = BuildUserContent(doc);

        HydeQuery? parsed = await AttemptAsync(userContent, stricter: false, ct).ConfigureAwait(false);
        if (parsed is not null)
        {
            return parsed;
        }

        // One stricter retry, then fall back — HyDE is an accelerator, never a hard dependency.
        parsed = await AttemptAsync(userContent, stricter: true, ct).ConfigureAwait(false);
        if (parsed is not null)
        {
            return parsed;
        }

        _logger.Warn("ImpactHyde", "HyDE generation failed to produce parseable JSON; using deterministic fallback.");
        return Fallback(doc);
    }

    private async Task<HydeQuery?> AttemptAsync(string userContent, bool stricter, CancellationToken ct)
    {
        var messages = new List<LlmMessage>
        {
            new("system", SystemPrompt),
            new("user", stricter ? userContent + "\n\n" + JsonInstruction + "\nReturn valid JSON only." : userContent + "\n\n" + JsonInstruction),
        };

        string? response = await _llm
            .CompleteAsync(new LlmRequest(_options.LlmModel, messages, MaxOutputTokens: 900, Temperature: 0, JsonOutput: true), ct)
            .ConfigureAwait(false);

        return response is null ? null : TryParse(response);
    }

    private static HydeQuery? TryParse(string response)
    {
        try
        {
            HydeResponse? body = JsonSerializer.Deserialize<HydeResponse>(StripFences(response), JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.ChangeSummary))
            {
                return null;
            }

            return new HydeQuery(
                body.ChangeSummary.Trim(),
                (body.SyntheticTestTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                body.SyntheticTestBody?.Trim() ?? string.Empty,
                (body.ExpandedTerms ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static string BuildUserContent(ChangeDocument doc)
    {
        var builder = new StringBuilder();

        AppendList(builder, "Changed user-visible strings:", doc.ChangedLiterals);
        AppendList(builder, "Changed public API signatures:", doc.PublicApiChanges);
        AppendInline(builder, "Changed symbols:", doc.ChangedSymbols);

        if (!string.IsNullOrWhiteSpace(doc.PrNarrative))
        {
            builder.Append("PR narrative:\n").Append(doc.PrNarrative).Append("\n\n");
        }

        AppendInline(builder, "Changed paths:", doc.PathTokens);
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string header, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.Append(header).Append('\n');
        foreach (string item in items)
        {
            builder.Append("- ").Append(item).Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendInline(StringBuilder builder, string header, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.Append(header).Append('\n').Append(string.Join(", ", items)).Append("\n\n");
    }

    private static HydeQuery Fallback(ChangeDocument doc)
    {
        // Deterministic offline query: the human literals already read like test language.
        IReadOnlyList<string> titles = doc.ChangedLiterals.Count > 0
            ? doc.ChangedLiterals.Take(8).ToList()
            : doc.ChangedSymbols.Take(8).ToList();

        string summary = !string.IsNullOrWhiteSpace(doc.PrNarrative)
            ? doc.PrNarrative!
            : doc.ChangedLiterals.Count > 0 ? doc.ChangedLiterals[0] : $"Change affecting {doc.AreaId}.";

        string body = string.Join(' ', doc.ChangedLiterals.Concat(new[] { doc.PrNarrative ?? string.Empty })
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        IReadOnlyList<string> expanded = doc.ChangedSymbols.Concat(doc.PathTokens)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();

        return new HydeQuery(summary, titles, body, expanded);
    }

    private sealed record HydeResponse(
        string? ChangeSummary, List<string>? SyntheticTestTitles, string? SyntheticTestBody, List<string>? ExpandedTerms);
}
