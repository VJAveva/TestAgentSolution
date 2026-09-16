using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Wire shape emitted by the per-platform <c>Vm-Ops.*.ps1</c> scripts. The existing revert contract signalled
/// success with an exit code alone, which cannot carry a snapshot id, a creation date, or a per-VM outcome —
/// all of which the orchestrator needs before it will destroy a baseline.
/// </summary>
public sealed class VmOpsEnvelope
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("results")] public List<VmOpsResult> Results { get; set; } = [];
}

public sealed class VmOpsResult
{
    [JsonPropertyName("vm")] public string Vm { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    [JsonPropertyName("power")] public string? Power { get; set; }
    [JsonPropertyName("snapshotId")] public string? SnapshotId { get; set; }
    [JsonPropertyName("snapshotName")] public string? SnapshotName { get; set; }
    [JsonPropertyName("createdUtc")] public DateTimeOffset? CreatedUtc { get; set; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
}

/// <summary>Parses the script envelope. Kept separate from the provider so the wire format is testable alone.</summary>
public static class VmOpsEnvelopeParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Extracts the envelope from captured stdout. Scripts interleave human-readable progress with the payload,
    /// so the LAST well-formed JSON object wins rather than requiring the script to emit nothing else.
    /// </summary>
    public static VmOpsEnvelope? Parse(IEnumerable<string> stdoutLines)
    {
        VmOpsEnvelope? found = null;
        foreach (var line in stdoutLines)
        {
            var text = line.Trim();
            if (text.Length < 2 || text[0] != '{' || text[^1] != '}') continue;

            try
            {
                var candidate = JsonSerializer.Deserialize<VmOpsEnvelope>(text, Options);
                if (candidate is not null) found = candidate;
            }
            catch (JsonException)
            {
                // A log line that merely looks like JSON is not an error; keep scanning.
            }
        }
        return found;
    }

    public static VmOpResult ToResult(VmOpsEnvelope envelope, IReadOnlyList<string> requestedVms)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var mapped = envelope.Results
            .Select(r => new VmResult(r.Vm, r.Ok, r.Error, r.Retryable)
            {
                Power = ParsePower(r.Power),
                Snapshot = r.SnapshotId is null && r.SnapshotName is null
                    ? null
                    : new SnapshotInfo(r.SnapshotId ?? "", r.SnapshotName ?? "", r.CreatedUtc, r.SizeBytes),
            })
            .ToList();

        // A VM the script never mentioned must not read as success; silence is not consent.
        foreach (var vm in requestedVms)
        {
            if (!mapped.Any(m => string.Equals(m.VmName, vm, StringComparison.OrdinalIgnoreCase)))
                mapped.Add(new VmResult(vm, false, envelope.Error ?? "No result reported for this VM."));
        }

        return new VmOpResult(mapped);
    }

    private static VmPower ParsePower(string? value) => value?.ToLowerInvariant() switch
    {
        "on" or "poweredon" => VmPower.On,
        "off" or "poweredoff" => VmPower.Off,
        "suspended" => VmPower.Suspended,
        _ => VmPower.Unknown,
    };
}
