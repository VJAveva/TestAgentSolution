using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Translates the agent's EVENT_WINDOWS_UPDATE firehose payload (JSON in <c>ExecutionEvent.detail</c>) into the
/// controller-side <see cref="NodeMaintenanceEventDto"/> that <see cref="INodeUpdateStatusStore"/> consumes. The
/// agent always sends the complete level state, so a single event fully describes the node.
/// </summary>
public static class WindowsUpdateEventMapper
{
    /// <summary>Serializer settings both the agent (write) and the controller (read) must use.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Parse the JSON detail into a maintenance event, or <c>null</c> if it is missing/malformed.</summary>
    public static NodeMaintenanceEventDto? TryMap(string nodeId, string? detailJson, DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(detailJson))
            return null;

        WindowsUpdatePayload? payload;
        try { payload = JsonSerializer.Deserialize<WindowsUpdatePayload>(detailJson, JsonOptions); }
        catch (JsonException) { return null; }
        if (payload is null) return null;

        return new NodeMaintenanceEventDto
        {
            NodeId = nodeId,
            Kind = payload.Kind == MaintenanceEventKind.Unspecified ? DeriveKind(payload) : payload.Kind,
            Source = payload.Source,
            Status = new WindowsUpdateStatusDto
            {
                RebootRequired = payload.RebootRequired,
                PendingCount = payload.PendingCount,
                Items = payload.Items,
                LastInstallUtc = payload.LastInstallUtc,
            },
            DetectedUtc = payload.DetectedUtc == default ? receivedUtc : payload.DetectedUtc,
        };
    }

    // Fallback for a payload that reports posture without naming the kind.
    private static MaintenanceEventKind DeriveKind(WindowsUpdatePayload p) =>
        p.RebootRequired ? MaintenanceEventKind.RebootRequired
        : p.PendingCount > 0 ? MaintenanceEventKind.UpdatePending
        : MaintenanceEventKind.RebootCleared;

    /// <summary>
    /// Extracts the KB article id embedded in an update title (e.g. "…(KB5034567)"), or "" when absent.
    /// Used by the agent when building an <see cref="UpdateItemDto"/> from a WUApi title.
    /// </summary>
    public static string ExtractKb(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        int i = title.IndexOf("KB", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            int j = i + 2;
            while (j < title.Length && char.IsDigit(title[j])) j++;
            if (j > i + 2) return title.Substring(i, j - i).ToUpperInvariant();
            i = title.IndexOf("KB", i + 2, StringComparison.OrdinalIgnoreCase);
        }
        return "";
    }
}
