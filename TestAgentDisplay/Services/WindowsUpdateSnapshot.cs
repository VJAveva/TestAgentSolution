using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestAgentDisplay.Services;

/// <summary>
/// Local mirror of the JSON the agent puts in <c>ExecutionEvent.Detail</c> for EVENT_WINDOWS_UPDATE.
/// Defined here rather than referenced from TestControllerGrpc.Core because this project ships
/// self-contained. Carries the complete level state on every message, never a delta.
///
/// Only the unambiguous scalar fields are modelled. The wire payload also has Kind/Source enums,
/// which are omitted deliberately: nothing here needs them, so this cannot break if their
/// serialized form (name vs number) ever changes.
/// </summary>
public sealed record WindowsUpdateSnapshot
{
    [JsonPropertyName("rebootRequired")]
    public bool RebootRequired { get; init; }

    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; init; }

    [JsonPropertyName("lastInstallUtc")]
    public DateTimeOffset? LastInstallUtc { get; init; }

    [JsonPropertyName("detectedUtc")]
    public DateTimeOffset DetectedUtc { get; init; }

    [JsonPropertyName("items")]
    public List<WindowsUpdateItem> Items { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parses the event detail. Returns false rather than throwing: a malformed payload
    /// from one agent must not break the display for the rest of the fleet.</summary>
    public static bool TryParse(string? detailJson, out WindowsUpdateSnapshot snapshot)
    {
        snapshot = new WindowsUpdateSnapshot();
        if (string.IsNullOrWhiteSpace(detailJson)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<WindowsUpdateSnapshot>(detailJson, Options);
            if (parsed is null) return false;
            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One-line operator summary, e.g. "Reboot required (3 pending)".</summary>
    public string Summarize()
    {
        if (RebootRequired && PendingCount > 0) return $"Reboot required ({PendingCount} pending)";
        if (RebootRequired) return "Reboot required";
        if (PendingCount > 0) return $"{PendingCount} update(s) pending";
        return "Up to date";
    }
}

public sealed record WindowsUpdateItem
{
    [JsonPropertyName("kbId")]
    public string KbId { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>Installed | Failed | Pending.</summary>
    [JsonPropertyName("result")]
    public string Result { get; init; } = "";

    [JsonPropertyName("resultCode")]
    public string ResultCode { get; init; } = "";
}
