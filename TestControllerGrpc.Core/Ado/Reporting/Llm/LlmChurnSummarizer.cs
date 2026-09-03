using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// LLM-backed <see cref="ILlmChangeSummarizer"/> using a two-level map→reduce over real code diffs:
///   • MAP (per component): grounds the model in the component's commit diffs + metadata → a component summary.
///   • REDUCE (release):    combines the per-component summaries into an executive regression digest.
/// Every path falls back to the injected deterministic <see cref="IChurnSummarizer"/> on any failure or
/// empty model response, so a summary is always produced and nothing leaks when the model is unavailable.
/// </summary>
public sealed class LlmChurnSummarizer : ILlmChangeSummarizer
{
    private const string SystemPrompt =
        "You are a senior release-impact analyst for AVEVA System Platform QA. Summarize ONLY what the " +
        "provided diffs and metadata show — never invent behavior. Prefer concrete functional impact and " +
        "regression risk over restating file names. Output strictly valid JSON matching the requested schema.";

    private const string ComponentPrompt =
        "Summarize the code changes for this single component. Return JSON: " +
        "{\"summary\": string (2-4 sentences on what changed and its functional/regression impact), " +
        "\"riskLevel\": \"Low\"|\"Medium\"|\"High\", \"impactedAreas\": string[], \"suggestedTests\": string[]}.";

    private const string ReleasePrompt =
        "You are given per-component change summaries for a release scope. Produce an executive regression " +
        "digest. Return JSON: {\"headline\": string (one line), \"highlights\": string[] (3-6 bullets), " +
        "\"narrative\": string (a short paragraph prioritizing what to regress first)}.";

    private readonly IChangeDiffSource _diffs;
    private readonly ILlmClient _llm;
    private readonly IChurnSummarizer _fallback;
    private readonly LlmOptions _options;
    private readonly AdoOptions _ado;
    private readonly IAppLogger _logger;

    public LlmChurnSummarizer(
        IChangeDiffSource diffs,
        ILlmClient llm,
        IChurnSummarizer fallback,
        IOptions<LlmOptions> options,
        IOptions<AdoOptions> ado,
        IAppLogger logger)
    {
        _diffs = diffs;
        _llm = llm;
        _fallback = fallback;
        _options = options.Value;
        _ado = ado.Value;
        _logger = logger;
    }

    private string ComponentProject =>
        string.IsNullOrWhiteSpace(_ado.OmiProject) ? _ado.Project : _ado.OmiProject;

    public async Task<string> SummarizeComponentAsync(SubsystemRow row, CancellationToken ct)
    {
        try
        {
            var grounding = await BuildComponentGroundingAsync(row, ct);
            var json = await _llm.CompleteAsync(new LlmRequest(
                _options.MapModel,
                [new LlmMessage("system", SystemPrompt), new LlmMessage("user", ComponentPrompt + "\n\n" + grounding)],
                _options.MaxOutputTokens, _options.Temperature, JsonOutput: true), ct);

            var summary = ExtractString(json, "summary");
            return string.IsNullOrWhiteSpace(summary) ? _fallback.SummarizeComponent(row) : summary!;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("Llm", $"Component summary failed for {row.Component}; using offline fallback.", ex);
            return _fallback.SummarizeComponent(row);
        }
    }

    public async Task<ChurnSummary> SummarizeReleaseAsync(ChurnReport report, CancellationToken ct)
    {
        if (report.Rows.Count == 0)
            return _fallback.Summarize(report);

        try
        {
            var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentComponentSummaries));
            var componentSummaries = await Task.WhenAll(report.Rows.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try { return (row.Component, Summary: await SummarizeComponentAsync(row, ct)); }
                finally { gate.Release(); }
            }));

            var sb = new StringBuilder();
            sb.Append("Scope: ").Append(report.ScopeLabel).Append(" — ").AppendLine(report.RangeText);
            sb.Append("Components changed: ").Append(report.Rows.Count).AppendLine();
            sb.AppendLine();
            foreach (var (comp, summary) in componentSummaries)
                sb.Append("- ").Append(comp).Append(": ").AppendLine(summary);

            var json = await _llm.CompleteAsync(new LlmRequest(
                _options.ReduceModel,
                [new LlmMessage("system", SystemPrompt), new LlmMessage("user", ReleasePrompt + "\n\n" + sb)],
                _options.MaxOutputTokens, _options.Temperature, JsonOutput: true), ct);

            var headline = ExtractString(json, "headline");
            var narrative = ExtractString(json, "narrative");
            var highlights = ExtractStringArray(json, "highlights");

            if (string.IsNullOrWhiteSpace(headline) && string.IsNullOrWhiteSpace(narrative))
                return _fallback.Summarize(report);

            return new ChurnSummary(headline ?? "", highlights, narrative ?? "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("Llm", "Release summary failed; using offline fallback.", ex);
            return _fallback.Summarize(report);
        }
    }

    private async Task<string> BuildComponentGroundingAsync(SubsystemRow row, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Component: ").Append(row.Component)
          .Append(" (Category: ").Append(row.Category)
          .Append(", Risk: ").Append(row.RiskTier).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(row.Repository)) sb.Append("Repository: ").AppendLine(row.Repository);
        if (row.RegressionAreas is { Count: > 0 }) sb.Append("Regression areas: ").AppendLine(string.Join(", ", row.RegressionAreas));
        if (row.UseCases is { Count: > 0 }) sb.Append("Use cases: ").AppendLine(string.Join(", ", row.UseCases));
        sb.AppendLine();

        var changes = row.Changes
            .Where(c => c.Kind != RegressionChangeKind.Automated)
            .Take(_options.MaxChangesPerComponent);

        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append("### Change [").Append(change.Kind).Append("] ").AppendLine(change.Summary);
            if (change.WorkItems.Count > 0)
                sb.Append("Work items: ").AppendLine(string.Join("; ", change.WorkItems.Select(w => $"{w.Kind} {w.Id}: {w.Title}")));

            if (LooksLikeSha(change.ChangeId) && !string.IsNullOrWhiteSpace(row.Repository))
            {
                try
                {
                    var diff = await _diffs.GetCommitDiffAsync(ComponentProject, row.Repository, change.ChangeId, ct);
                    foreach (var f in diff.Files)
                    {
                        sb.Append("File: ").Append(f.Path).Append(" (").Append(f.ChangeType).Append(')')
                          .AppendLine(f.Truncated ? " [truncated]" : "");
                        sb.AppendLine(f.Patch);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warn("Llm", $"Diff fetch failed for {Short(change.ChangeId)}: {ex.Message}");
                }
            }
            else if (change.FilePaths.Count > 0)
            {
                sb.Append("Files: ").AppendLine(string.Join(", ", change.FilePaths.Take(20)));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool LooksLikeSha(string id) =>
        id.Length is >= 7 and <= 40 && id.All(Uri.IsHexDigit);

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string? ExtractString(string? json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> ExtractStringArray(string? json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch (JsonException) { }
        return [];
    }
}
