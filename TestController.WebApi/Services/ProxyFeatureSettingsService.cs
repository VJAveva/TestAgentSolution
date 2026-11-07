using System.Net.Http;
using System.Text.Json;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;

namespace TestController.WebApi.Services;

/// <summary>
/// The controller owns Features.json and the audit log, so the web tier forwards saves to it instead of
/// writing the file under the IIS identity.
/// </summary>
public sealed class ProxyFeatureSettingsService(ControllerProxyService proxy) : IFeatureSettingsService
{
    public async Task<FeatureSaveResult> SaveAsync(
        IReadOnlyDictionary<string, bool> desired, IUserContext? user, string? authorizationHeader, CancellationToken ct)
    {
        if (!proxy.IsConfigured)
            return new FeatureSaveResult(false, "No controller is configured to save feature settings to.", []);

        var body = JsonSerializer.Serialize(new { features = desired });
        var response = await proxy.ForwardWithBodyAsync(HttpMethod.Put, "/api/features", authorizationHeader, body, "application/json");
        if (response is null)
            return new FeatureSaveResult(false, "The controller could not be reached; nothing was saved.", []);

        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        var root = doc.RootElement;
        if (response.IsSuccessStatusCode)
        {
            var changes = root.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [];
            return new FeatureSaveResult(true, root.TryGetProperty("message", out var m) ? m.GetString() ?? "Saved." : "Saved.", changes);
        }

        var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
        return new FeatureSaveResult(false, detail ?? $"The controller refused the change ({(int)response.StatusCode}).", []);
    }
}
