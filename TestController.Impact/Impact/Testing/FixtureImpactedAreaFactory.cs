using System.Globalization;

namespace TestControllerGrpc.Core.Impact.Testing;

/// <summary>
/// Builds <see cref="ImpactedArea"/> instances from churn-workbook-shaped CSV (P26), turning the team's
/// existing artefact into reproducible fixtures. Columns, in order: AreaId, DisplayName, Subsystem, Vob,
/// ChangedPaths (semicolon-separated), DeclaredRegressionAreas (semicolon-separated), RiskTier, LinesAdded,
/// LinesDeleted, FilesTouched, CommitCount, DistinctAuthorCount, LastChangedUtc.
/// </summary>
public static class FixtureImpactedAreaFactory
{
    private const int ColumnCount = 13;

    /// <summary>Parses every non-empty, non-header data row of a CSV document into impacted areas.</summary>
    public static IReadOnlyList<ImpactedArea> FromCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        return csv
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("AreaId", StringComparison.OrdinalIgnoreCase))
            .Select(FromCsvLine)
            .ToList();
    }

    /// <summary>Parses a single CSV data row into an impacted area.</summary>
    public static ImpactedArea FromCsvLine(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        string[] fields = line.Split(',');
        if (fields.Length < ColumnCount)
        {
            throw new FormatException($"Expected {ColumnCount} columns, found {fields.Length}: '{line}'.");
        }

        var churn = new ChurnMetrics(
            ParseInt(fields[7]), ParseInt(fields[8]), ParseInt(fields[9]), ParseInt(fields[10]), ParseInt(fields[11]),
            DateTimeOffset.TryParse(fields[12], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last)
                ? last
                : DateTimeOffset.UnixEpoch);

        return new ImpactedArea(
            fields[0].Trim(), fields[1].Trim(), NullIfEmpty(fields[2]), NullIfEmpty(fields[3]),
            SplitList(fields[4]), SplitList(fields[5]), ParseRiskTier(fields[6]), churn);
    }

    private static IReadOnlyList<string> SplitList(string value)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static RiskTier ParseRiskTier(string value)
        => Enum.TryParse(value.Trim(), ignoreCase: true, out RiskTier tier) ? tier : RiskTier.Unmapped;
}
