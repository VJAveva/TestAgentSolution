using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>Thrown when an Azure DevOps REST call fails after retries.</summary>
public sealed class AdoApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public AdoApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Typed HttpClient wrapping the Azure DevOps REST API (base address
/// https://dev.azure.com/{org}/{project}/_apis/...). Retries 429/5xx honoring
/// Retry-After, up to 3 attempts; never retries 401/403 (Phase C3).
/// </summary>
public sealed class AdoClient
{
    private const int MaxRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAdoTokenProvider _tokenProvider;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;

    public AdoClient(HttpClient http, IAdoTokenProvider tokenProvider, IOptions<AdoOptions> options, IAppLogger logger)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.Organization))
            _http.BaseAddress = new Uri(_options.BaseUrl);
    }

    /// <summary>Project-scoped API path, e.g. "build/builds/{id}/changes".</summary>
    public string ProjectApiPath(string relativePath) =>
        $"{Uri.EscapeDataString(_options.Project)}/_apis/{relativePath}";

    /// <summary>Project-scoped API path for an explicit project (e.g. the OMI project).</summary>
    public string ProjectApiPath(string project, string relativePath) =>
        $"{Uri.EscapeDataString(project)}/_apis/{relativePath}";

    /// <summary>Organization-scoped API path (no project segment), e.g. "wit/workitemsbatch".</summary>
    public string OrgApiPath(string relativePath) => $"_apis/{relativePath}";

    /// <summary>Fetches a raw (non-JSON) response body, e.g. build log text. Returns "" on non-success.</summary>
    public async Task<string> GetStringAsync(string relativePathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", await _tokenProvider.GetAuthHeaderAsync(ct));
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn("Ado", $"GetString {relativePathAndQuery} returned {(int)response.StatusCode}.");
            return "";
        }
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<T> GetAsync<T>(string relativePathAndQuery, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePathAndQuery);
            request.Headers.TryAddWithoutValidation("Authorization", await _tokenProvider.GetAuthHeaderAsync(ct));

            _logger.Info("Ado", $"[{correlationId}] GET {_http.BaseAddress}{relativePathAndQuery} (attempt {attempt})");
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxRetries)
                    throw new AdoApiException($"[{correlationId}] Network failure calling ADO after {MaxRetries} attempts.", inner: ex);
                await Task.Delay(BackoffFor(attempt, retryAfter: null), ct);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;
                throw new AdoApiException(
                    $"[{correlationId}] ADO auth failed ({status}). Credential: {_tokenProvider.Describe()}. " +
                    _tokenProvider.AuthFailureHint(status) + " " + SummarizeErrorBody(body),
                    response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt == MaxRetries)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new AdoApiException($"[{correlationId}] ADO returned {(int)response.StatusCode} after {MaxRetries} attempts. {SummarizeErrorBody(body)}", response.StatusCode);
                }
                await Task.Delay(BackoffFor(attempt, response.Headers.RetryAfter), ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new AdoApiException($"[{correlationId}] ADO call failed ({(int)response.StatusCode}): {SummarizeErrorBody(body)}", response.StatusCode);
            }

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            return result ?? throw new AdoApiException($"[{correlationId}] ADO returned an empty body.");
        }

        throw new AdoApiException($"[{correlationId}] Exhausted retries calling ADO.");
    }

    public async Task<T> PostAsync<T>(string relativePathAndQuery, object body, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePathAndQuery)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("Authorization", await _tokenProvider.GetAuthHeaderAsync(ct));

        _logger.Info("Ado", $"[{correlationId}] POST {_http.BaseAddress}{relativePathAndQuery}");
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var respBody = await response.Content.ReadAsStringAsync(ct);
            throw new AdoApiException($"[{correlationId}] ADO POST failed ({(int)response.StatusCode}): {SummarizeErrorBody(respBody)}", response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new AdoApiException($"[{correlationId}] ADO returned an empty body.");
    }

    /// <summary>
    /// Condenses an ADO error body to one readable line. A policy or sign-in block answers with a full HTML page
    /// carrying an inline base64 image, which otherwise floods the log, the health endpoint and the UI banner.
    /// The page title holds the actionable part (e.g. "VS403463: The conditional access policy ... has failed").
    /// </summary>
    internal static string SummarizeErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        if (body.TrimStart().StartsWith('<'))
        {
            var title = Regex.Match(body, "<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (title.Success)
            {
                var text = Collapse(WebUtility.HtmlDecode(title.Groups[1].Value));
                if (text.Length > 0)
                    return Truncate(text, 300);
            }
            return "(ADO returned an HTML page instead of JSON - the request was answered by a sign-in or policy page.)";
        }

        return Truncate(Collapse(body), 500);
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static TimeSpan BackoffFor(int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
            return delta;
        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }
}
