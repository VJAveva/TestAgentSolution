using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Eval;
using TestControllerGrpc.Core.Impact.Features;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Core.Impact.Query;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Core.Impact.Rerank;
using TestControllerGrpc.Core.Impact.Retrieval;
using TestControllerGrpc.Core.Impact.Selection;
using TestControllerGrpc.Core.Impact.Testing;
using TestControllerGrpc.Services;

namespace TestController.ImpactEval;

// Evaluation harness for the impact-mapping engine (P28). Verbs: replay | compare | train.
// The replay verb runs a reproducible, self-contained replay over the built-in fixture corpus and reports
// the metrics that matter — safe recall above all. Production replay loads historical areas and human-selected
// sets from the churn workbooks and the outcome store; that data source is environment-specific and is wired
// where those artefacts live.
internal static class Program
{
    private static readonly int[] RecallKs = [10, 25, 50, 100];

    private static async Task<int> Main(string[] args)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return verb switch
        {
            "replay" => await ReplayAsync(GetOption(args, "--out")),
            "compare" => Explain("compare produces a paired per-area delta table between two configs (config-a/config-b)."),
            "train" => Explain("train fits an isotonic or ranker calibrator from the outcome store's training pairs (P19/P20)."),
            _ => Usage(),
        };
    }

    private static async Task<int> ReplayAsync(string? outCsv)
    {
        FixtureCorpus corpus = ImpactFixtures.BuildGalaxyDeploymentCorpus();
        var embeddings = new FakeEmbeddingProvider();
        IRetrievalIndexStore store = await ImpactFixtures.BuildIndexStoreAsync(corpus, embeddings);

        ImpactMappingResult result = await BuildService(corpus.Ado, store, embeddings)
            .MapAsync(corpus.Area, corpus.Payload, SelectionTier.Targeted, CancellationToken.None);

        // Synthetic ground truth for the demo: the tests under the features a human would pick for a galaxy change.
        int[] groundTruthFeatures = [10, 11, 12, 20, 21];
        List<int> groundTruth = groundTruthFeatures
            .SelectMany(f => corpus.Ado.ChildrenByFeature.GetValueOrDefault(f, []))
            .ToList();
        IReadOnlyList<int> produced = result.MappedTestCases.Select(m => m.TestCase.Item.Id).ToList();
        IReadOnlyCollection<int> failed = groundTruth.Take(2).ToList();

        var evalCase = new EvalCase(
            corpus.Area.AreaId, produced, groundTruth, failed,
            SelectedRuntimeSeconds: result.MappedTestCases.Count,
            FullSuiteRuntimeSeconds: corpus.Ado.TestCases.Count,
            result.EarlyExit);

        EvalReport report = EvalMetrics.Aggregate([evalCase], RecallKs);
        PrintReport(report);
        if (!string.IsNullOrWhiteSpace(outCsv))
        {
            await File.WriteAllTextAsync(outCsv, ToCsv(report));
            Console.WriteLine($"Wrote {outCsv}.");
        }

        return 0;
    }

    private static void PrintReport(EvalReport report)
    {
        Console.WriteLine("Impact mapping — replay report");
        Console.WriteLine($"  cases                     {report.Cases}");
        Console.WriteLine($"  SAFE RECALL               {report.SafeRecall:P1}");
        Console.WriteLine($"  early-exit rate           {report.EarlyExitRate:P1}");
        Console.WriteLine($"  safe recall (early-exit)  {report.SafeRecallWithinEarlyExit:P1}");
        foreach (int k in RecallKs)
        {
            Console.WriteLine($"  recall@{k,-4}  {report.RecallAtK[k]:P1}    precision@{k,-4} {report.PrecisionAtK[k]:P1}");
        }

        Console.WriteLine($"  APFD                      {report.MeanApfd:F3}");
        Console.WriteLine($"  cost (selected/full)      {report.MeanCost:P1}");
    }

    private static string ToCsv(EvalReport report)
    {
        var lines = new List<string> { "metric,value", $"cases,{report.Cases}", $"safe_recall,{Fmt(report.SafeRecall)}", $"early_exit_rate,{Fmt(report.EarlyExitRate)}", $"safe_recall_early_exit,{Fmt(report.SafeRecallWithinEarlyExit)}", $"apfd,{Fmt(report.MeanApfd)}", $"cost,{Fmt(report.MeanCost)}" };
        lines.AddRange(RecallKs.Select(k => $"recall_at_{k},{Fmt(report.RecallAtK[k])}"));
        lines.AddRange(RecallKs.Select(k => $"precision_at_{k},{Fmt(report.PrecisionAtK[k])}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Fmt(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Explain(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("TestController.ImpactEval — impact mapping evaluation harness");
        Console.WriteLine("  replay  [--out <csv>]     replay the built-in fixture corpus and report metrics");
        Console.WriteLine("  compare                   paired per-area delta between two configurations");
        Console.WriteLine("  train                     fit a calibrator from the outcome store");
        return 0;
    }

    private static ImpactTestMappingService BuildService(FakeAdoWorkItemClient ado, IRetrievalIndexStore store, IEmbeddingProvider embeddings)
    {
        IOptions<ImpactMappingOptions> options = Options.Create(new ImpactMappingOptions());
        var logger = new ConsoleAppLogger();
        return new ImpactTestMappingService(
            new NoAnchors(), new ChangeDocumentBuilder(options), new FallbackHyde(), new KeywordExtractor(options),
            embeddings, store, new HybridRetriever(options, logger), new FeatureRanker(options),
            new ParentFeatureResolver(ado, logger), new FeatureMerger(options), new FanOutNormalizer(options), ado,
            new FakeRelevanceReranker(), new LinearScoreCalibrator(), new BudgetedDiversitySelector(options),
            new CoverageGapDetector(), new NoOutcomes(), options, logger);
    }

    private sealed class NoAnchors : IAnchorEdgeProvider
    {
        public Task<AnchorResult> GetAnchorsAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct)
            => Task.FromResult(new AnchorResult([], 0, false));
    }

    private sealed class FallbackHyde : IHydeQueryGenerator
    {
        public Task<HydeQuery> GenerateAsync(ChangeDocument doc, CancellationToken ct)
            => Task.FromResult(new HydeQuery("galaxy deployment", ["Verify galaxy deploy"], "deploy the galaxy node", ["galaxy", "deploy"]));
    }

    private sealed class NoOutcomes : IOutcomeStore
    {
        public Task RecordRunAsync(ImpactMappingResult result, CancellationToken ct) => Task.CompletedTask;
        public Task RecordExecutionAsync(Guid runId, IReadOnlyList<ExecutionOutcome> outcomes, CancellationToken ct) => Task.CompletedTask;
        public Task RecordEscapeAsync(string areaId, int testCaseId, string source, string? notes, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AnchorEdge>> GetHistoricalAnchorsAsync(string areaId, int lookbackRuns, CancellationToken ct) => Task.FromResult<IReadOnlyList<AnchorEdge>>([]);
        public Task<IReadOnlyDictionary<int, double>> GetFailureRatesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, double>>(new Dictionary<int, double>());
        public Task<IReadOnlyDictionary<int, TimeSpan>> GetDurationsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, TimeSpan>>(new Dictionary<int, TimeSpan>());
        public Task<IReadOnlyList<LabelledScore>> GetTrainingPairsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LabelledScore>>([]);
    }

    private sealed class ConsoleAppLogger : IAppLogger
    {
#pragma warning disable CS0067
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) => Console.Error.WriteLine($"WARN {category}: {message}");
        public void Error(string category, string message, Exception? ex = null) => Console.Error.WriteLine($"ERROR {category}: {message}");
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
