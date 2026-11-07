using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Features;

public sealed record FeatureSaveResult(
    bool Success, string Message, IReadOnlyList<string> Changes,
    string? BackupPath = null, DateTimeOffset? SavedAt = null);

/// <summary>Saves the Features map. Local on the controller; the standalone WebApi forwards to it.</summary>
public interface IFeatureSettingsService
{
    Task<FeatureSaveResult> SaveAsync(
        IReadOnlyDictionary<string, bool> desired, IUserContext? user, string? authorizationHeader, CancellationToken ct);
}

/// <summary>Writes Features.json in place: only "Features" entries change, unknown keys survive, backup first.</summary>
public static class FeatureFileWriter
{
    public static FeatureSaveResult Save(
        string path, IReadOnlyDictionary<string, bool> desired, Func<DateTimeOffset>? clock = null)
    {
        var unknown = desired.Keys.FirstOrDefault(k => !FeatureKeys.All.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            return new FeatureSaveResult(false, $"'{unknown}' is not a toggleable feature.", []);

        JsonObject root;
        string? original = null;
        try
        {
            if (File.Exists(path))
            {
                original = File.ReadAllText(path);
                root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? throw new JsonException("the file does not contain a JSON object.");
            }
            else
            {
                root = new JsonObject { ["TeamName"] = "", ["Features"] = new JsonObject() };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new FeatureSaveResult(false, $"Could not read '{path}': {ex.Message}. Fix or delete it first.", []);
        }

        var featuresKey = root.Select(p => p.Key).FirstOrDefault(k => k.Equals("Features", StringComparison.OrdinalIgnoreCase));
        if (featuresKey is null || root[featuresKey] is not JsonObject features)
        {
            features = new JsonObject();
            root[featuresKey ?? "Features"] = features;
        }

        var changes = new List<string>();
        foreach (var (key, on) in desired)
        {
            var existing = features.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            var wasOn = existing is null || features[existing] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
            if (wasOn == on && existing is not null) continue;
            features[existing ?? key] = on;
            if (wasOn != on) changes.Add($"{key} {(on ? "on" : "off")}");
        }

        if (changes.Count == 0)
            return new FeatureSaveResult(true, "No feature changes to save.", []);

        var newLine = original?.Contains("\r\n", StringComparison.Ordinal) == false ? "\n" : "\r\n";
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = newLine,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + newLine;

        var now = (clock ?? (() => DateTimeOffset.Now))();
        string? backup = null;
        var temp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (original is not null)
            {
                backup = $"{path}.bak-{now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: true);
            }
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException) { }
            return new FeatureSaveResult(false, $"Could not write '{path}': {ex.Message}", [], backup);
        }

        return new FeatureSaveResult(true, $"Saved at {now:HH:mm:ss} ({string.Join(", ", changes)}).", changes, backup, now);
    }
}

/// <summary>Controller-side save: writes the file, reloads the gate at once, and audits each change.</summary>
public sealed class FeatureSettingsService(
    IFeatureGate gate, IAuditWriter? auditWriter = null, IAppLogger? logger = null, Func<DateTimeOffset>? clock = null)
    : IFeatureSettingsService
{
    public Task<FeatureSaveResult> SaveAsync(
        IReadOnlyDictionary<string, bool> desired, IUserContext? user, string? authorizationHeader, CancellationToken ct)
    {
        var result = FeatureFileWriter.Save(gate.ConfigPath, desired, clock);
        if (!result.Success)
        {
            logger?.Warn("Features", $"Feature settings not saved: {result.Message}");
            return Task.FromResult(result);
        }

        if (result.Changes.Count == 0) return Task.FromResult(result);

        // Reload now rather than waiting for the file watcher, so the caller sees the new state.
        if (gate is FeatureGate concrete) concrete.Reload();

        var who = user?.DisplayName is { Length: > 0 } name ? name : user?.UserId ?? "unknown user";
        foreach (var change in result.Changes)
        {
            var parts = change.Split(' ');
            logger?.Info("Features", $"{who} turned {parts[0]} {parts[1]}.");
            auditWriter?.Enqueue(new AuditEntry
            {
                UserId = user?.UserId,
                GuestId = user?.GuestId,
                RoleAtTime = Enum.TryParse<Role>(user?.Roles.FirstOrDefault(), out var role) ? role : null,
                ActionName = nameof(Permission.System_ConfigureFeatures),
                ResourceId = parts[0],
                Allowed = true,
                ReasonCode = $"feature-{parts[1]}",
                TimestampUtc = DateTime.UtcNow,
                ClientKind = user?.ClientKind ?? ClientKind.Wpf,
            });
        }

        return Task.FromResult(result);
    }
}
