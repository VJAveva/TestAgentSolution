using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over the Azure OpenAI Chat Completions REST API. Keeps source-code
/// diffs inside the configured Azure resource (tenant data residency). The API key is read from
/// the environment variable named by <see cref="LlmOptions.ApiKeyEnvVarName"/>; prompt bodies are
/// never logged. Returns null on any failure so callers can fall back to the offline summarizer.
/// </summary>
public sealed class AzureOpenAiClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly IAppLogger _logger;

    public AzureOpenAiClient(HttpClient http, IOptions<LlmOptions> options, IAppLogger logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        if (_options.RequestTimeoutSeconds > 0)
            _http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
    }

    public async Task<string?> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(request.Model))
        {
            _logger.Warn("Llm", "Azure OpenAI endpoint/model not configured — skipping summarization.");
            return null;
        }

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVarName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.Warn("Llm", $"API key env var '{_options.ApiKeyEnvVarName}' is not set — skipping summarization.");
            return null;
        }

        var url = $"{_options.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(request.Model)}" +
                  $"/chat/completions?api-version={Uri.EscapeDataString(_options.ApiVersion)}";

        // Note: the o-series models use "max_completion_tokens" and reject "temperature";
        // this scaffolding targets the gpt-4.x/4o family. Adjust per selected deployment.
        var payload = new Dictionary<string, object?>
        {
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
        };
        if (request.JsonOutput)
            payload["response_format"] = new { type = "json_object" };

        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: JsonOptions),
            };
            httpReq.Headers.TryAddWithoutValidation("api-key", apiKey);

            using var resp = await _http.SendAsync(httpReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warn("Llm", $"Azure OpenAI returned {(int)resp.StatusCode} for model {request.Model}.");
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct);
            return body?.Choices?.FirstOrDefault()?.Message?.Content;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("Llm", "Azure OpenAI call failed.", ex);
            return null;
        }
    }

    private sealed record ChatResponse(List<Choice>? Choices);
    private sealed record Choice(ChatMessage? Message);
    private sealed record ChatMessage(string? Content);
}
