using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Anchors;
using TestControllerGrpc.Core.Impact.Features;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Core.Impact.Query;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Core.Impact.Rerank;
using TestControllerGrpc.Core.Impact.Retrieval;
using TestControllerGrpc.Core.Impact.Selection;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact;

/// <summary>Which side of the single-writer contract a host takes when registering the impact engine (P25).</summary>
public enum ImpactHostRole
{
    /// <summary>Reads the index and serves mappings, but never rebuilds it (the WPF host).</summary>
    Reader,

    /// <summary>Reads and owns index maintenance — at most one process per SQLite file (the WebApi host).</summary>
    ReaderWriter,
}

/// <summary>Composition root for the impact-mapping engine (P25).</summary>
public static class ImpactServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole impact engine. Providers are chosen from configuration so every optional dependency
    /// has a degradation default (Null embeddings, PassThrough rerank, Linear calibration, Null LLM). The index
    /// maintenance background service is registered ONLY for the <see cref="ImpactHostRole.ReaderWriter"/> host —
    /// two writers against one SQLite file is the failure mode this parameter exists to prevent. Impact services
    /// are Singleton (per the codebase convention) and reach their databases through context factories.
    /// </summary>
    public static IServiceCollection AddImpactMapping(this IServiceCollection services, IConfiguration config, ImpactHostRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        IConfigurationSection section = config.GetSection(ImpactMappingOptions.SectionName);
        var options = new ImpactMappingOptions();
        section.Bind(options);
        services.Configure<ImpactMappingOptions>(section);

        // Storage location is resolved once, outside the source tree, and the directories are provisioned
        // eagerly so the first write does not fail on a missing folder.
        services.TryAddSingleton<IImpactIndexPathProvider>(sp =>
        {
            var provider = new ImpactIndexPathProvider(config, sp.GetService<IAppLogger>());
            provider.EnsureIndexRootExists();
            return provider;
        });
        services.TryAddSingleton<IImpactIndexHealthCheck, ImpactIndexHealthCheck>();

        // Network persistence: the learning store is the only impact data that cannot be regenerated, so it
        // is mirrored to a share that outlives controller VM snapshot reverts.
        services.Configure<ImpactPersistenceOptions>(section.GetSection("Persistence"));
        services.TryAddSingleton<IImpactPersistenceService, ImpactPersistenceService>();

        // Separate databases: the rebuildable index cache and the durable learning store. Paths come from
        // the provider (absolute) — a relative DatabasePath resolves against the working directory, which is
        // how the index previously landed inside the repo.
        services.AddDbContextFactory<ImpactIndexDbContext>((sp, builder) =>
            builder.UseSqlite($"Data Source={sp.GetRequiredService<IImpactIndexPathProvider>().IndexFilePath}"));
        services.AddDbContextFactory<OutcomeDbContext>((sp, builder) =>
            builder.UseSqlite($"Data Source={sp.GetRequiredService<IImpactIndexPathProvider>().OutcomeFilePath}"));

        // ADO work-item client — builds on the host's existing AdoClient, which AddAdoRegressionIngest only
        // registers when Ado:Enabled=true. Registered via a factory (opaque to ValidateOnBuild) so a host with
        // ValidateOnBuild=true still starts when ADO is disabled, degrading to the null client (no external
        // work items) instead of failing DI validation on the unregistered AdoClient.
        services.TryAddSingleton<IAdoWorkItemClient>(sp =>
        {
            AdoClient? adoClient = sp.GetService<AdoClient>();
            return adoClient is null
                ? new NullAdoWorkItemClient()
                : new AdoImpactWorkItemClient(
                    adoClient,
                    sp.GetRequiredService<IOptions<ImpactMappingOptions>>(),
                    sp.GetRequiredService<IAppLogger>());
        });

        // Embeddings: Azure (cached) when an endpoint is configured, otherwise the null provider.
        if (!string.IsNullOrWhiteSpace(options.Index.EmbeddingEndpoint))
        {
            services.AddHttpClient<AzureOpenAiEmbeddingProvider>();
            services.TryAddSingleton<IEmbeddingProvider>(sp => new CachingEmbeddingProvider(sp.GetRequiredService<AzureOpenAiEmbeddingProvider>()));
        }
        else
        {
            services.TryAddSingleton<IEmbeddingProvider>(NullEmbeddingProvider.Instance);
        }

        // LLM client fallback; a host that registers a real Azure OpenAI client wins via TryAdd ordering.
        services.TryAddSingleton<ILlmClient, NullLlmClient>();

        // Rerank + HyDE.
        if (options.Rerank.EnableLlmRerank)
        {
            services.TryAddSingleton<IRelevanceReranker, LlmRelevanceReranker>();
        }
        else
        {
            services.TryAddSingleton<IRelevanceReranker, PassThroughReranker>();
        }

        services.TryAddSingleton<IHydeQueryGenerator, HydeQueryGenerator>();

        // Calibration by scoring mode (isotonic is fitted offline in P28, so it is not a startup choice).
        if (string.Equals(options.Learning.ScoringMode, "Ranker", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IScoreCalibrator, RankerScoreCalibrator>();
        }
        else
        {
            services.TryAddSingleton<IScoreCalibrator, LinearScoreCalibrator>();
        }

        services.TryAddSingleton<IFeatureVectorExtractor, FeatureVectorExtractor>();

        // Stores + index.
        services.TryAddSingleton<IOutcomeStore, OutcomeStore>();
        services.TryAddSingleton<IRetrievalIndexStore, RetrievalIndexStore>();
        services.TryAddSingleton<RetrievalIndexBuilder>();

        // Resolved through a factory so a host without the ADO component map still starts, degrading to no
        // declared edges rather than failing DI validation.
        services.TryAddSingleton<IDeclaredMappingSource>(sp =>
        {
            IComponentBuildMap? map = sp.GetService<IComponentBuildMap>();
            return map is null
                ? NullDeclaredMappingSource.Instance
                : new ComponentMapDeclaredMappingSource(
                    map,
                    sp.GetRequiredService<IRetrievalIndexStore>(),
                    sp.GetRequiredService<IOptions<ImpactMappingOptions>>(),
                    sp.GetRequiredService<IAppLogger>());
        });

        // Stateless components.
        services.TryAddSingleton<IChangeDocumentBuilder, ChangeDocumentBuilder>();
        services.TryAddSingleton<IKeywordExtractor, KeywordExtractor>();
        services.TryAddSingleton<IHybridRetriever, HybridRetriever>();
        services.TryAddSingleton<IFeatureRanker, FeatureRanker>();
        services.TryAddSingleton<IParentFeatureResolver, ParentFeatureResolver>();
        services.TryAddSingleton<IFeatureMerger, FeatureMerger>();
        services.TryAddSingleton<IFanOutNormalizer, FanOutNormalizer>();
        services.TryAddSingleton<IAnchorEdgeProvider, AnchorEdgeProvider>();
        services.TryAddSingleton<IBudgetedSelector, BudgetedDiversitySelector>();
        services.TryAddSingleton<ICoverageGapDetector, CoverageGapDetector>();

        // Orchestrator.
        services.TryAddSingleton<IImpactTestMappingService, ImpactTestMappingService>();

        // Regression adapter: projects a SubsystemRow's changes to display-ready Test Case matches
        // for the Code Churn workbook ("Impacted Test Cases" sheet) and the grid row-expand.
        services.TryAddSingleton<IRegressionImpactMatcher, RegressionImpactMatcher>();

        // Index maintenance runs only on the writer host.
        if (role == ImpactHostRole.ReaderWriter)
        {
            services.AddHostedService<IndexMaintenanceService>();
        }

        // Persistence runs on EVERY host: a reader still accumulates its own run outcomes, and losing those
        // to a snapshot revert is the failure this exists to prevent. Safe to run everywhere because each
        // host writes its own folder and the merge is an idempotent set union, not a read-modify-write.
        services.AddHostedService<ImpactPersistenceWorker>();

        return services;
    }
}
