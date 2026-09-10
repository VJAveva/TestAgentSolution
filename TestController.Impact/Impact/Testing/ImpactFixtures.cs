using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Text;

namespace TestControllerGrpc.Core.Impact.Testing;

/// <summary>Serves pre-built kind-scoped snapshots without a database, for tests and fixtures (P26).</summary>
public sealed class InMemoryRetrievalIndexStore(IndexSnapshot featureSnapshot, IndexSnapshot testCaseSnapshot) : IRetrievalIndexStore
{
    /// <inheritdoc />
    public Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
        => Task.FromResult(kind == IndexKind.Feature ? featureSnapshot : testCaseSnapshot);
}

/// <summary>A generated fixture corpus plus the ids of the deliberately-planted edge cases (P26).</summary>
public sealed record FixtureCorpus(
    FakeAdoWorkItemClient Ado,
    ImpactedArea Area,
    ChangePayload Payload,
    IReadOnlyList<int> TitleMismatchedFeatureIds,
    IReadOnlyList<int> OrphanTestCaseIds,
    IReadOnlyList<int> CorroboratedFeatureIds,
    int HubFeatureId,
    IReadOnlyList<int> NearDuplicateTestCaseIds);

/// <summary>
/// Deterministic impact-mapping fixtures (P26). Builds a ~40-feature / ~300-test-case corpus with a realistic
/// link graph and deliberate edge cases: three features whose TITLES miss the area keyword but whose TEST
/// CASES do not (proving the back-reference branch earns its cost), five orphans, two features matched by both
/// branches (corroboration), one hub feature with 200 children (fan-out), and a cluster of ten near-identical
/// test cases (MMR). Everything is generated in code so it is reproducible and cheap to maintain.
/// </summary>
public static class ImpactFixtures
{
    /// <summary>Builds the galaxy-deployment corpus and its populated ADO fake.</summary>
    /// <param name="requirementStyleTitles">
    /// Titles test cases "FR &lt;id&gt;" and moves the descriptive text into Description, modelling corpora where
    /// the title is a bare requirement id and carries no matchable vocabulary.
    /// </param>
    /// <param name="dropDescriptions">Discards descriptions, reproducing the behaviour before System.Description was indexed.</param>
    public static FixtureCorpus BuildGalaxyDeploymentCorpus(
        bool requirementStyleTitles = false, bool dropDescriptions = false)
    {
        var ado = new FakeAdoWorkItemClient();
        var children = new Dictionary<int, List<int>>();

        int[] titleMismatch = [10, 11, 12];
        var mismatchTitles = new Dictionary<int, string> { [10] = "Runtime Node Manager", [11] = "Security Guard Service", [12] = "Reporting Engine" };
        int[] corroborated = [20, 21];
        const int hub = 30;
        const int clusterFeature = 40;

        for (int id = 1; id <= 40; id++)
        {
            string title = titleMismatch.Contains(id) ? mismatchTitles[id]
                : id == hub ? "Common Framework"
                : corroborated.Contains(id) ? $"Galaxy Deploy Feature {id}"
                : id % 2 == 0 ? $"Galaxy Feature {id}"
                : $"Deploy Feature {id}";
            ado.Features[id] = new FeatureCandidate(
                new AdoWorkItemRef(id, "Feature", title, "Proj\\Deploy", "Active", 1), $"{title} description", FeatureDiscoveryPath.None, [], 0);
        }

        void AddTestCase(int id, string title, string steps, int? parent)
        {
            // Requirement-style corpus: the title is a bare id, the steps are boilerplate procedure, and all
            // the matchable functional prose lives in the description.
            string effectiveTitle = requirementStyleTitles ? $"FR {id}" : title;
            string effectiveSteps = requirementStyleTitles ? "Execute the documented procedure and record the result." : steps;
            string? description = requirementStyleTitles && !dropDescriptions ? $"{title}. {steps}" : null;

            ado.TestCases[id] = new TestCaseCandidate(
                new AdoWorkItemRef(id, "Test Case", effectiveTitle, "Proj\\Deploy", "Design", 1), effectiveSteps, "Automated", parent, [],
                Description: description);
            if (parent is int p)
            {
                (children.TryGetValue(p, out List<int>? list) ? list : children[p] = []).Add(id);
            }
        }

        // Hub feature: 200 children, none mentioning the area keyword (so it does not flood retrieval).
        for (int k = 1; k <= 200; k++)
        {
            AddTestCase(7000 + k, $"Common framework test {k}", "Run a common framework check.", hub);
        }

        // Near-identical cluster under the cluster feature. Overlaps only two change signals (galaxy, deploy)
        // so it is graded 2 — subject to MMR diversity rather than pinned by the grade-3 recall safety net.
        var nearDuplicates = new List<int>();
        for (int k = 1; k <= 10; k++)
        {
            int id = 8000 + k;
            nearDuplicates.Add(id);
            AddTestCase(id, "Deploy galaxy smoke test", "Open console. Deploy galaxy. Verify galaxy is online.", clusterFeature);
        }

        // Title-mismatched features: galaxy lives only in the test cases.
        foreach (int feature in titleMismatch)
        {
            for (int k = 1; k <= 3; k++)
            {
                AddTestCase((feature * 100) + k, $"Galaxy path scenario {k}", "Exercise the galaxy deployment path and verify the galaxy node.", feature);
            }
        }

        // Corroborated features: galaxy in both the title and the test cases.
        foreach (int feature in corroborated)
        {
            for (int k = 1; k <= 3; k++)
            {
                AddTestCase((feature * 100) + k, $"Galaxy deploy verification {k}", "Deploy galaxy and verify the deployment.", feature);
            }
        }

        // Remaining features get a couple of generic test cases each.
        foreach (int feature in Enumerable.Range(1, 40).Except([.. titleMismatch, .. corroborated, hub, clusterFeature]))
        {
            for (int k = 1; k <= 2; k++)
            {
                AddTestCase((feature * 100) + k, $"{ado.Features[feature].Item.Title} regression {k}", "Run regression for the feature.", feature);
            }
        }

        // Orphans: galaxy test cases with no parent feature.
        var orphans = new List<int>();
        for (int n = 1; n <= 5; n++)
        {
            int id = 9000 + n;
            orphans.Add(id);
            AddTestCase(id, $"Orphan galaxy scenario {n}", "Verify galaxy behaviour end to end.", parent: null);
        }

        foreach ((int featureId, List<int> childIds) in children)
        {
            ado.ChildrenByFeature[featureId] = childIds;
            ado.Features[featureId] = ado.Features[featureId] with { ChildTestCaseCount = childIds.Count };
        }

        var area = new ImpactedArea(
            "GALAXY-DEPLOY", "Galaxy Deploy", "Deploy", "GalaxyVob",
            ["src/Deploy/GalaxyEngine.cs"], ["Galaxy Deployment"], RiskTier.High,
            new ChurnMetrics(50, 10, 3, 2, 1, DateTimeOffset.UnixEpoch));

        var payload = new ChangePayload(
            1234, "Fix galaxy deploy", "Fixes the galaxy deployment node startup", [],
            [new FileDiff("src/Deploy/GalaxyEngine.cs",
                [new DiffHunk(1, 3, "+    public void DeployGalaxyNode(string vob)\n+    { _log.Info(\"Deploy the galaxy node now\"); }")])],
            [hub]);

        return new FixtureCorpus(ado, area, payload, titleMismatch, orphans, corroborated, hub, nearDuplicates);
    }

    /// <summary>Builds an in-memory index store from a corpus, tokenizing and embedding every document.</summary>
    public static async Task<IRetrievalIndexStore> BuildIndexStoreAsync(
        FixtureCorpus corpus, IEmbeddingProvider embeddings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(embeddings);

        var keywords = new ImpactMappingOptions.KeywordOptions();
        var documents = new List<(int Id, IndexKind Kind, string Text, int ChildCount)>();
        documents.AddRange(corpus.Ado.Features.Values.Select(f => (f.Item.Id, IndexKind.Feature, IndexTextComposer.ForFeature(f), f.ChildTestCaseCount)));
        documents.AddRange(corpus.Ado.TestCases.Values.Select(t => (t.Item.Id, IndexKind.TestCase, IndexTextComposer.ForTestCase(t), 0)));

        Dictionary<int, IReadOnlyDictionary<string, int>> termsById =
            documents.ToDictionary(d => d.Id, d => Tokenizer.TokenizeWithCounts(d.Text, keywords));

        var documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((int id, _, _, _) in documents)
        {
            foreach (string term in termsById[id].Keys)
            {
                documentFrequencies[term] = documentFrequencies.GetValueOrDefault(term) + 1;
            }
        }

        double averageLength = documents.Count > 0 ? documents.Average(d => (double)termsById[d.Id].Values.Sum()) : 1;
        int documentCount = documents.Count;

        IReadOnlyList<float[]> vectors = await embeddings.EmbedAsync(documents.Select(d => d.Text).ToArray(), ct).ConfigureAwait(false);
        var vectorById = new Dictionary<int, float[]?>();
        for (int i = 0; i < documents.Count; i++)
        {
            vectorById[documents[i].Id] = i < vectors.Count && vectors[i].Length > 0 ? vectors[i] : null;
        }

        SnapshotDocument ToDoc((int Id, IndexKind Kind, string Text, int ChildCount) d)
            => new(d.Id, d.Kind, termsById[d.Id].Values.Sum(), d.ChildCount, termsById[d.Id], vectorById[d.Id]);

        List<SnapshotDocument> featureDocs = documents.Where(d => d.Kind == IndexKind.Feature).Select(ToDoc).ToList();
        List<SnapshotDocument> testCaseDocs = documents.Where(d => d.Kind == IndexKind.TestCase).Select(ToDoc).ToList();

        return new InMemoryRetrievalIndexStore(
            new IndexSnapshot(documentCount, averageLength, documentFrequencies, featureDocs),
            new IndexSnapshot(documentCount, averageLength, documentFrequencies, testCaseDocs));
    }
}
