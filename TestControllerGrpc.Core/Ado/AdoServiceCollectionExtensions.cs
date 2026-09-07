using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// DI wiring for the real Azure DevOps ingest pipeline (Phase E4). Registered from both hosts
/// (TestController.Api's AddControllerApi and TestControllerGrpc's App.xaml.cs) since each has
/// its own DI container. Binds "Ado" config; only registers IRegressionDataProvider when
/// Ado:Enabled=true, otherwise the caller's own TryAddSingleton&lt;IRegressionDataProvider,
/// MockRegressionDataProvider&gt;() wins.
/// </summary>
public static class AdoServiceCollectionExtensions
{
    public static IServiceCollection AddAdoRegressionIngest(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new AdoOptions();
            cfg.GetSection("Ado").Bind(opts);
            return Microsoft.Extensions.Options.Options.Create(opts);
        });

        // Startup diagnostic: logs whether mock or live ADO is active and why. Registered
        // unconditionally so it also reports the mock-is-active case.
        services.AddHostedService<AdoStartupDiagnostics>();

        // Component map + source catalog are registered unconditionally so the Regression tab's status bar
        // and definition picker work even when ADO ingest is disabled (they read config + the vobs map only).
        services.TryAddSingleton<IComponentBuildMap, ComponentBuildMap>();
        services.TryAddSingleton<IComponentCategorizer, ComponentCategorizer>();
        services.TryAddSingleton<IRegressionSourceCatalog, RegressionSourceCatalog>();

        // Churn report / AI summary are pure functions over the rows — no ADO dependency, so they're
        // available for export + email even when live ingest is off (e.g. against the mock provider).
        services.TryAddSingleton<Reporting.IChurnSummarizer, Reporting.ChurnSummarizer>();
        services.TryAddSingleton<Reporting.IChurnReportBuilder, Reporting.ChurnReportBuilder>();

        // Work-item policy (Task exclusion + Feature roll-up). Validated here so a bad
        // CodeChurn:WorkItemPolicy section fails at startup naming the setting, not mid-report.
        services.TryAddSingleton(sp =>
            Reporting.CodeChurnWorkItemPolicy.Load(sp.GetRequiredService<IConfiguration>()));

        // Resolved lazily: IWorkItemQueries only exists when ADO ingest is enabled below.
        services.TryAddSingleton<Reporting.IWorkItemHierarchyResolver>(sp =>
        {
            var queries = sp.GetService<IWorkItemQueries>();
            return queries is null
                ? new Reporting.NullWorkItemHierarchyResolver()
                : new Reporting.WorkItemHierarchyResolver(
                    queries, sp.GetRequiredService<Reporting.CodeChurnWorkItemPolicy>());
        });

        services.TryAddSingleton<Reporting.ICodeChurnReportBuilder, Reporting.CodeChurnReportBuilder>();

        // Async LLM change-summarizer. Resolves the diff-grounded LlmChurnSummarizer when the LLM is
        // enabled + wired (ILlmClient/IChangeDiffSource present), else an offline pass-through over the
        // deterministic summarizer. Registered unconditionally so callers always resolve one.
        services.TryAddSingleton<Reporting.Llm.ILlmChangeSummarizer>(sp =>
        {
            var fallback = sp.GetRequiredService<Reporting.IChurnSummarizer>();
            var llmOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<Reporting.Llm.LlmOptions>>();
            var llm = sp.GetService<Reporting.Llm.ILlmClient>();
            var diffs = sp.GetService<Reporting.Llm.IChangeDiffSource>();
            if (llmOptions?.Value.Enabled == true && llm is not null && diffs is not null)
            {
                return new Reporting.Llm.LlmChurnSummarizer(
                    diffs, llm, fallback, llmOptions,
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdoOptions>>(),
                    sp.GetRequiredService<IAppLogger>());
            }
            return new Reporting.Llm.OfflineChangeSummarizer(fallback);
        });

        // Interactive Entra sign-in (WPF). Registered unconditionally so the Regression tab's sign-in UI can
        // always resolve it; it reports IsAvailable=false unless Ado is enabled with AuthMode=Interactive.
        services.TryAddSingleton<InteractiveTokenProvider>();
        services.TryAddSingleton<IInteractiveAdoAuthenticator>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdoOptions>>().Value;
            return opts.Enabled && opts.AuthMode == AdoAuthMode.Interactive
                ? sp.GetRequiredService<InteractiveTokenProvider>()
                : new UnavailableAdoAuthenticator();
        });

        // Only wire up the ADO pipeline (HttpClient, token provider, queries) when explicitly
        // enabled — avoids attempting credential resolution (which can throw) on hosts that
        // haven't configured Ado: at all.
        var enabled = IsEnabled(services);
        if (!enabled)
            return services;

        services.TryAddSingleton<IAdoTokenProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdoOptions>>().Value;
            return opts.AuthMode switch
            {
                AdoAuthMode.ServicePrincipal => new ServicePrincipalTokenProvider(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdoOptions>>()),
                AdoAuthMode.Interactive => sp.GetRequiredService<InteractiveTokenProvider>(),
                _ => new PatTokenProvider(sp.GetRequiredService<IAdoCredentialStore>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdoOptions>>()),
            };
        });
        services.TryAddSingleton<IAdoCredentialStore, EnvironmentAdoCredentialStore>();

        services.AddHttpClient<AdoClient>();
        services.TryAddSingleton<IBuildQueries, BuildQueries>();
        services.TryAddSingleton<IGitQueries, GitQueries>();
        services.TryAddSingleton<IWorkItemQueries, WorkItemQueries>();
        services.TryAddSingleton<ITestPlanQueries, TestPlanQueries>();
        services.TryAddSingleton<IRepositoryResolver, RepositoryResolver>();
        services.TryAddSingleton<IBuildManifestSource, BuildLogManifestSource>();
        services.TryAddSingleton<IComponentBuildMap, ComponentBuildMap>();
        services.TryAddSingleton<AdoChangeTranslator>();
        services.TryAddSingleton<SpBuildImpactCollector>();
        services.TryAddSingleton<ComponentChangeCollector>();

        // LLM-backed code-change summarization (CodeChurn). Scaffolding — off unless Ado:Llm:Enabled=true.
        if (IsLlmEnabled(services))
        {
            services.TryAddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var opts = new Reporting.Llm.LlmOptions();
                cfg.GetSection(Reporting.Llm.LlmOptions.SectionName).Bind(opts);
                return Microsoft.Extensions.Options.Options.Create(opts);
            });
            services.AddHttpClient<Reporting.Llm.AzureOpenAiClient>();
            services.TryAddSingleton<Reporting.Llm.ILlmClient>(sp => sp.GetRequiredService<Reporting.Llm.AzureOpenAiClient>());

            // Diff source, optionally wrapped by a persistent cache keyed by immutable {repo}:{commit}.
            services.TryAddSingleton<Reporting.Llm.AdoChangeDiffSource>();
            services.TryAddSingleton<Reporting.Llm.ICommitDiffCache, Reporting.Llm.FileCommitDiffCache>();
            services.TryAddSingleton<Reporting.Llm.IChangeDiffSource>(sp =>
            {
                var inner = sp.GetRequiredService<Reporting.Llm.AdoChangeDiffSource>();
                var cacheDiffs = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Reporting.Llm.LlmOptions>>().Value.CacheDiffs;
                return cacheDiffs
                    ? new Reporting.Llm.CachedChangeDiffSource(inner, sp.GetRequiredService<Reporting.Llm.ICommitDiffCache>())
                    : inner;
            });
        }

        services.TryAddSingleton<IRegressionDataProvider, AdoRegressionDataProvider>();

        return services;
    }

    private static bool IsEnabled(IServiceCollection services)
    {
        using var provisional = services.BuildServiceProvider();
        var cfg = provisional.GetService<IConfiguration>();
        return cfg?.GetSection("Ado")?.GetValue<bool>("Enabled") ?? false;
    }

    private static bool IsLlmEnabled(IServiceCollection services)
    {
        using var provisional = services.BuildServiceProvider();
        var cfg = provisional.GetService<IConfiguration>();
        return cfg?.GetSection(Reporting.Llm.LlmOptions.SectionName)?.GetValue<bool>("Enabled") ?? false;
    }
}
