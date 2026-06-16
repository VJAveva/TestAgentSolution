using System.Net.Http;
using System.Net.Http.Json;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF-side HTTP client for audit query endpoints (GET /api/audit, GET /api/audit/export).
/// Singleton. Per Phase 10.
/// </summary>
public class AuditClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    public AuditClient(IHttpClientFactory httpClientFactory, AuthClient authClient, IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _authClient = authClient;
        _logger = logger;
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_authClient.Token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authClient.Token);
        return request;
    }

    /// <summary>Queries audit entries with pagination and optional filters.</summary>
    public async Task<AuditPageDto?> QueryAsync(AuditQueryRequest filter, CancellationToken ct = default)
    {
        try
        {
            var qs = BuildQueryString(filter);
            using var request = AuthorizedRequest(HttpMethod.Get, $"/api/audit{qs}");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AuditPageDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.Error("AuditClient", "Query failed", ex);
            return null;
        }
    }

    /// <summary>Downloads audit CSV export to file.</summary>
    public async Task<bool> ExportCsvAsync(AuditQueryRequest filter, string filePath, CancellationToken ct = default)
    {
        try
        {
            var qs = BuildQueryString(filter);
            using var request = AuthorizedRequest(HttpMethod.Get, $"/api/audit/export{qs}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var fileStream = System.IO.File.Create(filePath);
            await response.Content.CopyToAsync(fileStream, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("AuditClient", "CSV export failed", ex);
            return false;
        }
    }

    private static string BuildQueryString(AuditQueryRequest f)
    {
        var parts = new List<string>();
        if (f.From.HasValue) parts.Add($"from={f.From.Value:O}");
        if (f.To.HasValue) parts.Add($"to={f.To.Value:O}");
        if (!string.IsNullOrWhiteSpace(f.UserId)) parts.Add($"userId={Uri.EscapeDataString(f.UserId)}");
        if (!string.IsNullOrWhiteSpace(f.ActionName)) parts.Add($"actionName={Uri.EscapeDataString(f.ActionName)}");
        if (f.Decision.HasValue) parts.Add($"decision={f.Decision.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(f.ResourceId)) parts.Add($"resourceId={Uri.EscapeDataString(f.ResourceId)}");
        if (!string.IsNullOrWhiteSpace(f.ReasonCode)) parts.Add($"reasonCode={Uri.EscapeDataString(f.ReasonCode)}");
        parts.Add($"page={f.Page}");
        parts.Add($"pageSize={f.PageSize}");
        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class AuditQueryRequest
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? UserId { get; set; }
    public string? ActionName { get; set; }
    public bool? Decision { get; set; }
    public string? ResourceId { get; set; }
    public string? ReasonCode { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 200;
}

public sealed class AuditPageDto
{
    public List<AuditEntryDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class AuditEntryDto
{
    public long AuditId { get; set; }
    public string? UserId { get; set; }
    public string? GuestId { get; set; }
    public string? RoleAtTime { get; set; }
    public string ActionName { get; set; } = "";
    public string? ResourceId { get; set; }
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string ClientKind { get; set; } = "";
    public string? CorrelationId { get; set; }
}
