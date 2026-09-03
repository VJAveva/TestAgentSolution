namespace TestControllerGrpc.Ado.Reporting.Llm;

/// <summary>
/// Configuration for the LLM-backed code-change summarizer, bound from "Ado:Llm".
/// Disabled by default — when off, the deterministic <c>ChurnSummarizer</c> remains in use.
/// The API key is NOT stored here; it is read from the environment variable named by
/// <see cref="ApiKeyEnvVarName"/> (mirrors how the ADO PAT is handled).
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Ado:Llm";

    /// <summary>Master switch. False keeps summarization on the offline deterministic path.</summary>
    public bool Enabled { get; set; }

    /// <summary>Azure OpenAI resource endpoint, e.g. "https://my-aoai.openai.azure.com".</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Azure OpenAI REST api-version, e.g. "2024-10-21".</summary>
    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>Deployment/model for the high-volume per-commit "map" step (cheap/fast).</summary>
    public string MapModel { get; set; } = "";

    /// <summary>Deployment/model for the component/release "reduce" step (stronger reasoning).</summary>
    public string ReduceModel { get; set; } = "";

    /// <summary>Env var holding the Azure OpenAI API key. Default: AZURE_OPENAI_API_KEY.</summary>
    public string ApiKeyEnvVarName { get; set; } = "AZURE_OPENAI_API_KEY";

    /// <summary>Max changed files diffed per commit (cost/token cap). Default: 8.</summary>
    public int MaxFilesPerCommit { get; set; } = 8;

    /// <summary>Max bytes of diff text sent per file (larger diffs are truncated). Default: 8000.</summary>
    public int MaxDiffBytesPerFile { get; set; } = 8000;

    /// <summary>Max changes (commits/PRs) diffed + summarized per component. Default: 5.</summary>
    public int MaxChangesPerComponent { get; set; } = 5;

    /// <summary>Max components summarized concurrently in the release reduce. Default: 4.</summary>
    public int MaxConcurrentComponentSummaries { get; set; } = 4;

    /// <summary>Max completion tokens requested per call. Default: 800.</summary>
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>Sampling temperature (0 = deterministic). Default: 0.</summary>
    public double Temperature { get; set; }

    /// <summary>Per-call HTTP timeout (seconds). Default: 60.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Cache immutable commit diffs to avoid re-fetching from ADO on repeat summaries. Default: true.</summary>
    public bool CacheDiffs { get; set; } = true;

    /// <summary>Directory for the on-disk diff cache. Empty/null → &lt;LogDir&gt;/llm-cache.</summary>
    public string? CacheDirectory { get; set; }
}
