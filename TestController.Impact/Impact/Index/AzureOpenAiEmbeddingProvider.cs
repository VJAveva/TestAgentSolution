using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// <see cref="IEmbeddingProvider"/> over the Azure OpenAI embeddings REST API (P08). Mirrors the existing
/// <c>AzureOpenAiClient</c>: raw <see cref="HttpClient"/> + <see cref="System.Text.Json"/>, API key read
/// from the configured environment variable, and graceful degradation — any misconfiguration or transport
/// failure returns empty vectors (so retrieval falls back to lexical) instead of throwing. Input texts are
/// never logged.
/// </summary>
public sealed class AzureOpenAiEmbeddingProvider : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ImpactMappingOptions.IndexOptions _options;
    private readonly IAppLogger _logger;

    /// <summary>Creates the provider from the impact-mapping index options.</summary>
    public AzureOpenAiEmbeddingProvider(HttpClient http, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options.Value.Index;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ModelId => _options.EmbeddingModel;

    /// <inheritdoc />
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.EmbeddingEndpoint);

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        if (!IsEnabled)
        {
            _logger.Warn("ImpactEmbedding", "Embedding endpoint not configured — returning empty vectors.");
            return EmptyVectors(inputs.Count);
        }

        string? apiKey = Environment.GetEnvironmentVariable(_options.EmbeddingApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.Warn("ImpactEmbedding", $"API key env var '{_options.EmbeddingApiKeyEnvVar}' is not set — returning empty vectors.");
            return EmptyVectors(inputs.Count);
        }

        string url = $"{_options.EmbeddingEndpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(ModelId)}" +
                     $"/embeddings?api-version={Uri.EscapeDataString(_options.EmbeddingApiVersion)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { input = inputs }, options: JsonOptions),
            };
            request.Headers.TryAddWithoutValidation("api-key", apiKey);

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn("ImpactEmbedding", $"Azure OpenAI embeddings returned {(int)response.StatusCode} for model {ModelId}.");
                return EmptyVectors(inputs.Count);
            }

            EmbeddingResponse? body = await response.Content
                .ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, ct).ConfigureAwait(false);
            if (body?.Data is null)
            {
                return EmptyVectors(inputs.Count);
            }

            // Reassemble by the server-provided index; never assume response order matches request order.
            var ordered = EmptyVectors(inputs.Count);
            foreach (EmbeddingDatum datum in body.Data)
            {
                if (datum.Index >= 0 && datum.Index < ordered.Length && datum.Embedding is not null)
                {
                    ordered[datum.Index] = datum.Embedding;
                }
            }

            return ordered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("ImpactEmbedding", "Azure OpenAI embeddings call failed.", ex);
            return EmptyVectors(inputs.Count);
        }
    }

    private static float[][] EmptyVectors(int count)
    {
        var vectors = new float[count][];
        Array.Fill(vectors, Array.Empty<float>());
        return vectors;
    }

    private sealed record EmbeddingResponse(List<EmbeddingDatum>? Data);

    private sealed record EmbeddingDatum(int Index, float[]? Embedding);
}
