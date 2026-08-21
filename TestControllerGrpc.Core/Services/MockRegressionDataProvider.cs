using System.Collections.Concurrent;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Deterministic, in-memory stand-in for the real Azure DevOps ingest pipeline
/// (docs/AzureIntegration/FEATURE-ARCHITECTURE.md Phases 0-5). Those phases need
/// Entra/PAT credentials and captured fixtures that don't exist in this repo yet
/// (see docs/AzureIntegration/Azure-DevOps-Integration-Guide.md Stage 0-1).
///
/// This provider exists so the Regression tab (R9 grid, R11-13 plan panel) can be
/// built and reviewed now, per RegressionTab-UI-Spec.md's own build order: "R9
/// first with static data — the grid is the feature, everything else adjusts it."
///
/// Replace this with a real implementation (backed by IImpactResolver over
/// SQLite + component-usecase-map.v2.json) once Phase 5 lands. The interface
/// shape (<see cref="IRegressionDataProvider"/>) is designed not to change.
///
/// Sample rows mirror the component list in docs/impact/component-usecase-map.v1.json
/// and deliberately reproduce the real gap-scoreboard shape from the architecture
/// doc §11: several subsystems with no suite mapped (NO SUITE badge), several with
/// only unlinked manual entries (dashed chips), a couple of low-confidence category
/// classifications, and one deliberately unresolved repository for the R14 status bar.
/// </summary>
public sealed class MockRegressionDataProvider : IRegressionDataProvider
{
    private const double MinutesPerAutomatedSuite = 12; // ADR-10: invented, label as estimate until measured from TRX
    private const double MinutesPerManualSuite = 25;

    private readonly IAppLogger _logger;
    private readonly List<SubsystemRow> _rows;
    private readonly ConcurrentDictionary<string, SubsystemRow> _bySubsystem;

    public MockRegressionDataProvider(IAppLogger logger)
    {
        _logger = logger;
        _rows = BuildSeedRows();
        _bySubsystem = new ConcurrentDictionary<string, SubsystemRow>(
            _rows.ToDictionary(r => r.Subsystem, r => r), StringComparer.OrdinalIgnoreCase);
    }

    public Task<ConsolidatedImpact> GetConsolidatedAsync(DateOnly from, DateOnly to, string? branch, CancellationToken ct)
    {
        var rows = _bySubsystem.Values.ToList();
        var changeCount = rows.Sum(r => r.Changes.Count);
        var fileCount = rows.Sum(r => r.TotalFilesModified);

        var summary = new RegressionSummary(
            ScopeLabel: "Custom",
            RangeText: $"{from:yyyy-MM-dd} \u2192 {to:yyyy-MM-dd}",
            ChangeCount: changeCount,
            SubsystemCount: rows.Count,
            FileCount: fileCount,
            WeeklyActivity: [4, 7, 3, 9, 6, 12, 5, 8]);

        return Task.FromResult(new ConsolidatedImpact(summary, rows));
    }

    public Task<RegressionScope> GetScopeAsync(DateOnly from, DateOnly to, RegressionCategoryKind? category, string? branch, CancellationToken ct)
    {
        var rows = _bySubsystem.Values
            .Where(r => category is null || r.Category == category || r.Category == RegressionCategoryKind.Both)
            .ToList();

        var runtime = BuildPlanColumn(rows.Where(r => r.Category is RegressionCategoryKind.Runtime or RegressionCategoryKind.Both));
        var config = BuildPlanColumn(rows.Where(r => r.Category is RegressionCategoryKind.Config or RegressionCategoryKind.Both));
        var unmapped = rows
            .Where(r => r.AutomatedSuites.Count == 0 && r.ManualSuites.Count == 0)
            .Select(r => r.Subsystem)
            .ToList();

        return Task.FromResult(new RegressionScope(runtime, config, unmapped, ParallelAgentCount: 4));
    }

    public Task<RegressionSyncStatus> GetSyncStatusAsync(CancellationToken ct)
    {
        return Task.FromResult(new RegressionSyncStatus(
            State: "Mock data (Phase 6 UI-first — ADO ingest not yet wired)",
            LastSyncUtc: null,
            MapVersion: "1.0-draft",
            UnresolvedRepositories: ["ArchVisDev"]));
    }

    public Task ApplySuiteEditAsync(RegressionSuiteEdit edit, CancellationToken ct)
    {
        _bySubsystem.AddOrUpdate(edit.Subsystem,
            _ => throw new KeyNotFoundException($"Unknown subsystem '{edit.Subsystem}'."),
            (_, row) =>
            {
                var target = edit.IsManual ? row.ManualSuites : row.AutomatedSuites;
                List<RegressionSuiteRef> updated;

                if (edit.Remove)
                {
                    updated = target.Where(s => !string.Equals(s.SuiteId, edit.SuiteId, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                else
                {
                    updated = [.. target, new RegressionSuiteRef(edit.SuiteId, IsLinked: false, Url: null, RegressionEvidenceKind.Observed)];
                }

                var newRow = edit.IsManual
                    ? row with { ManualSuites = updated }
                    : row with { AutomatedSuites = updated };

                return newRow with { EstimatedMinutes = Estimate(newRow.AutomatedSuites.Count, newRow.ManualSuites.Count) };
            });

        _logger.Info("Regression", $"Suite edit applied: {edit.Subsystem} {(edit.IsManual ? "manual" : "automated")} '{edit.SuiteId}' remove={edit.Remove}");
        return Task.CompletedTask;
    }

    private static double Estimate(int automatedCount, int manualCount) =>
        automatedCount * MinutesPerAutomatedSuite + manualCount * MinutesPerManualSuite;

    private static RegressionPlanColumn BuildPlanColumn(IEnumerable<SubsystemRow> rows)
    {
        var list = rows.ToList();
        return new RegressionPlanColumn(
            Subsystems: list.Count,
            AutomatedSuites: list.Sum(r => r.AutomatedSuites.Count),
            ManualSuites: list.Sum(r => r.ManualSuites.Count),
            Gaps: list.Count(r => r.AutomatedSuites.Count == 0 && r.ManualSuites.Count == 0),
            EstimatedMinutes: list.Sum(r => r.EstimatedMinutes));
    }

    private static List<SubsystemRow> BuildSeedRows()
    {
        var now = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

        SubsystemRow Row(
            string component, string subsystem, RegressionCategoryKind category, RegressionEvidenceKind confidence,
            string riskTier, IReadOnlyList<RegressionSuiteRef> auto, IReadOnlyList<RegressionSuiteRef> manual,
            params (string file, string summary, RegressionWorkItemKind kind, int wiId, string title)[] changes)
        {
            var changeRefs = changes.Select((c, i) => new RegressionChangeRef(
                ChangeId: $"{subsystem}-C{i + 1}",
                Summary: c.summary,
                ObservedUtc: now.AddDays(-i),
                FilePaths: [c.file],
                WorkItems: [new RegressionWorkItemRef(c.wiId, c.kind, c.title, Url: null)])).ToList();

            return new SubsystemRow(
                Component: component,
                Subsystem: subsystem,
                Category: category,
                CategoryConfidence: confidence,
                FilesModified: changeRefs.SelectMany(c => c.FilePaths).ToList(),
                TotalFilesModified: changeRefs.Sum(c => c.FilePaths.Count),
                Changes: changeRefs,
                RiskTier: riskTier,
                AutomatedSuites: auto,
                ManualSuites: manual,
                EstimatedMinutes: Estimate(auto.Count, manual.Count),
                IsEstimate: true);
        }

        RegressionSuiteRef Auto(string id) => new(id, IsLinked: true, Url: null, RegressionEvidenceKind.Observed);
        RegressionSuiteRef ManualLinked(string id) => new(id, IsLinked: true, Url: null, RegressionEvidenceKind.Declared);
        RegressionSuiteRef ManualFree(string text) => new(text, IsLinked: false, Url: null, RegressionEvidenceKind.Assumed);

        return
        [
            Row("AAMxCore", "AAMxCore/Lmx", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Observed, "High",
                [Auto("FR829777.EACRuntime")], [],
                ("Lmx/Lmx.cpp", "Fix message-channel deadlock on redundancy failover", RegressionWorkItemKind.Bug, 481920, "Deadlock in Lmx during failover")),

            Row("AASysObjects", "AASysObjects/Scheduler", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Declared, "Medium",
                [Auto("FR1359724.WP.IO.Scaling")], [ManualLinked("TC-88213")],
                ("Scheduler/Scheduler.cpp", "Adjust scan-class jitter compensation", RegressionWorkItemKind.Ims, 482011, "Scan jitter on high-density galaxies")),

            Row("ArchSvc", "ArchSvc/MXDataService", RegressionCategoryKind.Both, RegressionEvidenceKind.Observed, "High",
                [], [ManualFree("manual smoke pass, no suite id")],
                ("MxDataServiceHost/Program.cs", "Bump gRPC keepalive timeout", RegressionWorkItemKind.Story, 482055, "MxData host disconnects under load")),

            Row("ArchestraObj", "ArchestraObj/AnalogDevicePrimitive", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Assumed, "Medium",
                [], [],
                ("AnalogDevicePrimitive/AnalogDevicePrimitive.cpp", "Add ROC clamp on bad-quality input", RegressionWorkItemKind.Bug, 482090, "ROC alarm floods on comm loss")),

            Row("PFServer", "PFServer/CRLink", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Observed, "High",
                [Auto("FR552210.CRLink.Grpc")], [],
                ("CRLink/ProtGrpc/CRLinkGrpcService.cpp", "Retire DCOM fallback path", RegressionWorkItemKind.Feature, 482121, "Migrate CRLink transport to gRPC-only")),

            Row("IDEExtensions", "IDEExtensions/GalaxyExplorer", RegressionCategoryKind.Config, RegressionEvidenceKind.Declared, "Low",
                [], [ManualLinked("TC-77410")],
                ("GalaxyExplorer/MainForm.cs", "Update ribbon icon set", RegressionWorkItemKind.Story, 482145, "Refresh IDE ribbon iconography")),

            Row("MagellanProduct", "MagellanProduct/AlarmHistory", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Observed, "High",
                [], [],
                ("AlarmHistory/AlarmExtensionRuntime.cpp", "Fix event timestamp rollover at DST boundary", RegressionWorkItemKind.Bug, 482177, "Alarm timestamps off by 1h at DST")),

            Row("DeviceIntegration", "DeviceIntegration/OPCClientObj", RegressionCategoryKind.Config, RegressionEvidenceKind.Assumed, "Medium",
                [Auto("FR339981.OPCClient.Reconnect")], [],
                ("OPCClientObj/OpcEnumLibInterop/OpcEnum.cpp", "Widen reconnect backoff ceiling", RegressionWorkItemKind.Ims, 482203, "OPC client reconnect storm")),

            Row("Cybersecurity", "Cybersecurity/xxSecurity", RegressionCategoryKind.Config, RegressionEvidenceKind.Observed, "High",
                [], [ManualFree("pen-test regression, tracked in spreadsheet")],
                ("xxSecurity/xxSecurity.cpp", "Tighten role-token validation", RegressionWorkItemKind.Bug, 482240, "Role token accepted after revoke")),

            Row("ObjectFramework", "ObjectFramework/AttributesTabHost", RegressionCategoryKind.Unclassified, RegressionEvidenceKind.Assumed, "Low",
                [], [],
                ("AttributesTabHost/AttributesTab/AttributesTab.csproj", "Bump target framework reference", RegressionWorkItemKind.Other, 482266, "Housekeeping: TFM bump")),

            Row("Common", "Common/BootstrapProxyClient", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Observed, "Medium",
                [Auto("FR118820.BootstrapProxy.Relay")], [],
                ("AVEVA.AppServer.BootstrapProxy.Client/RelayClient.cs", "Add retry-after honouring on relay 429", RegressionWorkItemKind.Bug, 482299, "Relay client ignores Retry-After")),

            Row("AppObjCommon", "AppObjCommon/ROCAlarmsPrimitive", RegressionCategoryKind.Runtime, RegressionEvidenceKind.Declared, "Medium",
                [], [],
                ("ROCAlarmsPrimitive/ROCAlarmsPrimitive.cpp", "Fix rate-of-change alarm hysteresis", RegressionWorkItemKind.Bug, 482318, "ROC alarm chatters near threshold")),
        ];
    }
}
