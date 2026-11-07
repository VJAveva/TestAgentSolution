using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>Section of a layered pipeline-config.json a value lives in.</summary>
public enum ParameterLayer
{
    Global,
    Profile,
    Pipeline,

    /// <summary>Supplied by GlobalVariables.json, a different file. Never written by this writer.</summary>
    GlobalVariables,
}

/// <summary>One change to a layered config. A null <see cref="Value"/> removes the key.</summary>
public sealed record ParameterEdit(string Key, string? Value, ParameterLayer Layer);

public sealed record ParameterSaveResult(bool Success, string Message, string? BackupPath = null, DateTimeOffset? SavedAt = null);

/// <summary>
/// Writes edits into a layered JSON config in place: only the edited keys change, every other
/// section, key and key order is kept, and the previous file is backed up first.
/// </summary>
public static class ParameterFileWriter
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ParameterLayer LayerFromRank(int rank) => rank switch
    {
        <= (int)ParameterRank.Global => ParameterLayer.Global,
        <= (int)ParameterRank.ParameterFile => ParameterLayer.Profile,
        <= (int)ParameterRank.PipelinePin => ParameterLayer.Pipeline,
        _ => ParameterLayer.GlobalVariables,
    };

    public static ParameterSaveResult SaveJsonEdits(
        string filePath,
        string? profile,
        string? pipelineTag,
        IReadOnlyList<ParameterEdit> edits,
        Func<DateTimeOffset>? clock = null)
    {
        if (edits.Count == 0)
            return new ParameterSaveResult(true, "No parameter changes to save.");

        var foreign = edits.FirstOrDefault(e => e.Layer == ParameterLayer.GlobalVariables);
        if (foreign is not null)
            return new ParameterSaveResult(false,
                $"'{foreign.Key}' comes from GlobalVariables.json, not this file. Edit it in Global Variables.");
        if (edits.Any(e => e.Layer == ParameterLayer.Profile) && string.IsNullOrWhiteSpace(profile))
            return new ParameterSaveResult(false, "A Profile value was edited but no Profile is selected.");
        if (edits.Any(e => e.Layer == ParameterLayer.Pipeline) && string.IsNullOrWhiteSpace(pipelineTag))
            return new ParameterSaveResult(false, "A pipeline override was edited but the pipeline Tag is empty.");

        string original;
        JsonObject root;
        try
        {
            original = File.ReadAllText(filePath);
            root = JsonNode.Parse(original, documentOptions: ReadOptions) as JsonObject
                ?? throw new JsonException("the file does not contain a JSON object.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ParameterSaveResult(false, $"Could not read '{filePath}': {ex.Message}");
        }

        foreach (var edit in edits)
        {
            var section = SectionFor(root, edit.Layer, profile, pipelineTag, create: edit.Value is not null);
            if (section is null) continue;

            var existing = FindKey(section, edit.Key);
            if (edit.Value is null)
            {
                if (existing is not null) section.Remove(existing);
            }
            else
            {
                section[existing ?? edit.Key] = edit.Value;
            }
        }

        var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = newLine,
            // A local config file, not HTML: escaping '+' or '&' would rewrite values nobody edited.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + (original.EndsWith('\n') ? newLine : "");

        try
        {
            if (JsonSerializer.Deserialize<PipelineParameterConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is null)
                return new ParameterSaveResult(false, "The edited config did not round-trip; nothing was written.");
        }
        catch (JsonException ex)
        {
            return new ParameterSaveResult(false, $"The edited config is not valid; nothing was written: {ex.Message}");
        }

        var now = (clock ?? (() => DateTimeOffset.Now))();
        var backupPath = $"{filePath}.bak-{now:yyyyMMdd-HHmmss}";
        var tempPath = filePath + ".tmp";
        try
        {
            File.Copy(filePath, backupPath, overwrite: true);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return new ParameterSaveResult(false, $"Could not write '{filePath}': {ex.Message}", backupPath);
        }

        return new ParameterSaveResult(true,
            $"Saved to {filePath} at {now:HH:mm:ss} (backup {Path.GetFileName(backupPath)}).", backupPath, now);
    }

    private static JsonObject? SectionFor(
        JsonObject root, ParameterLayer layer, string? profile, string? pipelineTag, bool create) => layer switch
        {
            ParameterLayer.Global => Child(root, "Global", create),
            ParameterLayer.Profile => Child(Child(root, "Profiles", create), profile!, create),
            ParameterLayer.Pipeline => Child(Child(root, "Pipelines", create), pipelineTag!, create),
            _ => null,
        };

    // Section and key names are matched case-insensitively, the same way the reader binds them.
    private static JsonObject? Child(JsonObject? parent, string name, bool create)
    {
        if (parent is null) return null;
        var key = FindKey(parent, name);
        if (key is not null) return parent[key] as JsonObject;
        if (!create) return null;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
