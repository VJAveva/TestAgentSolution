using System.Diagnostics;
using System.Text.Json;

namespace TestController.LoadTests;

/// <summary>Single diagnostics-probe sample.</summary>
public readonly record struct ProbeSample(double LatencyMs, int SimAgentCount, int StatusCode);

/// <summary>
/// Polls the controller's <c>/api/health/diagnostics</c> endpoint and reports how
/// many simulated agents it currently sees plus the request latency — the proxy
/// for "does the controller stay responsive while the fleet heartbeats?".
/// </summary>
public sealed class ControllerProbe
{
    private readonly HttpClient _http;
    private readonly Uri _diagnosticsUri;

    public ControllerProbe(string controllerApiUrl)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _diagnosticsUri = new Uri(new Uri(controllerApiUrl.TrimEnd('/') + "/"), "api/health/diagnostics");
    }

    public async Task<ProbeSample> SampleAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var resp = await _http.GetAsync(_diagnosticsUri, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            return new ProbeSample(sw.Elapsed.TotalMilliseconds, 0, (int)resp.StatusCode);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var count = 0;
        if (doc.RootElement.TryGetProperty("agents", out var agents)
            && agents.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in agents.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var name)
                    && name.GetString() is { } n
                    && n.StartsWith("sim-agent-", StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }

        return new ProbeSample(sw.Elapsed.TotalMilliseconds, count, (int)resp.StatusCode);
    }
}
