using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestControllerGrpc.Core.Fir;

public sealed record FirPackLoadResult(IReadOnlyList<FirRulePack> Packs, IReadOnlyList<string> Errors);

/// <summary>
/// Loads rules\*.fir.json. One team's malformed pack must never stop another team's report, so a bad
/// file is skipped with a recorded error instead of throwing.
/// </summary>
public static class FirRulePackLoader
{
    public const string FileSuffix = ".fir.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static FirPackLoadResult LoadDirectory(string? directory)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new FirPackLoadResult([], errors);

        var packs = new List<FirRulePack>();
        foreach (var file in Directory.EnumerateFiles(directory, "*" + FileSuffix).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Parse(File.ReadAllText(file), Path.GetFileName(file), errors) is { } pack)
                    packs.Add(pack);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: could not be read - {ex.Message}");
            }
        }

        return new FirPackLoadResult(Order(packs), errors);
    }

    public static FirRulePack? Parse(string json, string source, List<string> errors)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<FirRulePack>(json, Options);
            if (pack is null)
            {
                errors.Add($"{source}: empty pack.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(pack.Id))
            {
                errors.Add($"{source}: pack has no id.");
                return null;
            }

            // A pack that declares nothing would claim every test it was offered.
            if (pack.AppliesTo.IsEmpty)
            {
                errors.Add($"{source}: pack '{pack.Id}' declares no appliesTo and will never be selected.");
                return null;
            }

            return pack;
        }
        catch (JsonException ex)
        {
            errors.Add($"{source}: not valid JSON - {ex.Message}");
            return null;
        }
    }

    /// <summary>Framework before product, then by id, so a report's pack list is reproducible.</summary>
    private static IReadOnlyList<FirRulePack> Order(IEnumerable<FirRulePack> packs)
        => [.. packs.OrderBy(p => p.Kind == FirPackKind.Framework ? 0 : 1).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
}
