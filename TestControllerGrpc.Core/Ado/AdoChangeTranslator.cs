using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado;

/// <summary>Raw inputs the translator needs for one build, already fetched by the caller (no I/O here).</summary>
public sealed record TranslatorBuildInput(
    int BuildId,
    IReadOnlyList<AdoBuildChangeDto> Changes,
    IReadOnlyDictionary<string, IReadOnlyList<(string Path, string ChangeType)>> FilesByChangeId,
    IReadOnlyList<AdoWorkItemDto> WorkItems);

/// <summary>
/// Pure mapping from raw ADO data to the Regression domain model (Phase D2). No I/O — all data is
/// pre-fetched by AdoRegressionDataProvider and passed in. Tags evidence "ado:build:{buildId}" /
/// confidence Observed per the architecture doc, and groups by (component, subsystem) resolved via
/// IRepositoryResolver.
/// </summary>
public sealed class AdoChangeTranslator
{
    private const double MinutesPerAutomatedSuite = 12; // ADR-10: invented estimate, same as mock provider
    private const double MinutesPerManualSuite = 25;

    private readonly IRepositoryResolver _resolver;

    public AdoChangeTranslator(IRepositoryResolver resolver) => _resolver = resolver;

    /// <summary>Groups translated changes into SubsystemRow list. Unresolved paths are collected separately.</summary>
    public (IReadOnlyList<SubsystemRow> Rows, IReadOnlyList<string> UnresolvedHints) Translate(TranslatorBuildInput input)
    {
        // ADO's builds/{id}/workitems endpoint returns work items for the whole build, not per-commit,
        // so every change in this build shares the same work item set (documented limitation).
        var buildWorkItems = input.WorkItems
            .Select(ToWorkItemRef)
            .ToList();

        var bySubsystem = new Dictionary<(string Component, string Subsystem), List<RegressionChangeRef>>();
        var unresolved = new HashSet<string>();

        foreach (var change in input.Changes)
        {
            if (!input.FilesByChangeId.TryGetValue(change.Id, out var files) || files.Count == 0)
                continue;

            var grouped = files.GroupBy(f => ExtractComponentHint(f.Path));
            foreach (var group in grouped)
            {
                var hint = group.Key;
                var componentId = _resolver.ResolveComponent(hint);
                if (componentId is null)
                {
                    unresolved.Add(hint);
                    continue;
                }

                var subsystem = ExtractSubsystemHint(group.First().Path) ?? componentId;
                var key = (componentId, subsystem);
                var changeRef = new RegressionChangeRef(
                    ChangeId: change.Id,
                    Summary: change.Message ?? "(no message)",
                    ObservedUtc: change.Timestamp ?? DateTimeOffset.UtcNow,
                    FilePaths: group.Select(f => f.Path).ToList(),
                    WorkItems: buildWorkItems);

                if (!bySubsystem.TryGetValue(key, out var list))
                    bySubsystem[key] = list = [];
                list.Add(changeRef);
            }
        }

        var rows = bySubsystem.Select(kvp => BuildRow(kvp.Key.Component, kvp.Key.Subsystem, kvp.Value)).ToList();
        return (rows, unresolved.ToList());
    }

    private static SubsystemRow BuildRow(string component, string subsystem, List<RegressionChangeRef> changes)
    {
        var files = changes.SelectMany(c => c.FilePaths).Distinct().ToList();
        // No automated/manual suite linkage yet — that requires ITestPlanQueries suite-name matching
        // (tracked gap, see TestPlanQueries.cs remarks). Real ingest starts with empty suite chips.
        return new SubsystemRow(
            Component: component,
            Subsystem: subsystem,
            Category: RegressionCategoryKind.Unclassified,
            CategoryConfidence: RegressionEvidenceKind.Observed,
            FilesModified: files.Take(20).ToList(),
            TotalFilesModified: files.Count,
            Changes: changes,
            RiskTier: "Unknown",
            AutomatedSuites: [],
            ManualSuites: [],
            EstimatedMinutes: 0,
            IsEstimate: true);
    }

    private static RegressionWorkItemRef ToWorkItemRef(AdoWorkItemDto dto) => new(
        Id: dto.Id,
        Kind: ParseWorkItemKind(dto.WorkItemType),
        Title: dto.Title ?? $"Work item {dto.Id}",
        Url: dto.Url,
        WorkItemType: dto.WorkItemType);

    private static RegressionWorkItemKind ParseWorkItemKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return RegressionWorkItemKind.Other;
        // IMS types vary by process template (e.g. "Internal IMS Request", "IMS Internal Request") — match the token.
        if (type.Contains("IMS", StringComparison.OrdinalIgnoreCase))
            return RegressionWorkItemKind.Ims;
        return type switch
        {
            "Bug" => RegressionWorkItemKind.Bug,
            "User Story" => RegressionWorkItemKind.Story,
            "Feature" => RegressionWorkItemKind.Feature,
            "Issue" => RegressionWorkItemKind.Ims,
            _ => RegressionWorkItemKind.Other,
        };
    }

    /// <summary>First path segment, e.g. "SysObj/Scheduler/Scheduler.cpp" -> "SysObj".</summary>
    private static string ExtractComponentHint(string path)
    {
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash > 0 ? trimmed[..slash] : trimmed;
    }

    /// <summary>Second path segment as a rough subsystem hint, e.g. "SysObj/Scheduler/Scheduler.cpp" -> "Scheduler".</summary>
    private static string? ExtractSubsystemHint(string path)
    {
        var parts = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }
}
