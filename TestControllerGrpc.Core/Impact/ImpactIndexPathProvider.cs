using System.IO;
using Microsoft.Extensions.Configuration;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// Resolves the impact database roots, first match wins:
/// <list type="number">
///   <item>Environment variable <c>IMPACT_INDEX_ROOT</c></item>
///   <item>Configuration key <c>ImpactMapping:IndexRoot</c></item>
///   <item><c>%ProgramData%\TestAgentSolution\ImpactIndex</c></item>
/// </list>
/// Resolution happens once and is cached, so the "which source won" log line appears once per process.
/// </summary>
public sealed class ImpactIndexPathProvider : IImpactIndexPathProvider
{
    public const string IndexFileName = "impact-index.db";
    public const string OutcomeFileName = "impact-outcomes.db";
    public const string EnvironmentVariableName = "IMPACT_INDEX_ROOT";

    private readonly IConfiguration? _config;
    private readonly IAppLogger? _logger;
    private readonly Lock _gate = new();

    private string? _indexRoot;
    private string? _learningRoot;

    public ImpactIndexPathProvider(IConfiguration? config = null, IAppLogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public string IndexRoot
    {
        get
        {
            EnsureResolved();
            return _indexRoot!;
        }
    }

    public string LearningRoot
    {
        get
        {
            EnsureResolved();
            return _learningRoot!;
        }
    }

    public string IndexFilePath => Path.Combine(IndexRoot, IndexFileName);

    public string OutcomeFilePath => Path.Combine(LearningRoot, OutcomeFileName);

    public bool IndexExists => File.Exists(IndexFilePath);

    public void EnsureIndexRootExists()
    {
        Directory.CreateDirectory(IndexRoot);
        Directory.CreateDirectory(LearningRoot);
    }

    /// <summary>The default root used when neither the environment variable nor configuration supplies one.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TestAgentSolution",
        "ImpactIndex");

    private void EnsureResolved()
    {
        if (_indexRoot is not null) return;

        lock (_gate)
        {
            if (_indexRoot is not null) return;

            var (root, source) = Resolve();
            var (learning, learningSource) = ResolveLearning(root);

            _learningRoot = learning;
            _indexRoot = root;
            _logger?.Info("ImpactIndex",
                $"Index root resolved from {source}: {root} (learning from {learningSource}: {learning})");
        }
    }

    private (string Root, string Source) Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return (Path.GetFullPath(fromEnv.Trim()), $"environment variable {EnvironmentVariableName}");

        var fromConfig = _config?[$"{ImpactMappingOptions.SectionName}:IndexRoot"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return (Path.GetFullPath(fromConfig.Trim()), $"configuration {ImpactMappingOptions.SectionName}:IndexRoot");

        return (DefaultRoot, "default (ProgramData)");
    }

    /// <summary>
    /// Learning data is durable and must never sit inside the rebuildable index root. An explicit
    /// <c>ImpactMapping:LearningRoot</c> wins; otherwise it becomes a sibling of the index root. The
    /// sibling result must stay absolute — a relative path would resolve against the working directory,
    /// which is the fragmentation bug this provider exists to remove.
    /// </summary>
    private (string Root, string Source) ResolveLearning(string indexRoot)
    {
        var fromConfig = _config?[$"{ImpactMappingOptions.SectionName}:LearningRoot"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return (Path.GetFullPath(fromConfig.Trim()), $"configuration {ImpactMappingOptions.SectionName}:LearningRoot");

        var parent = Path.GetDirectoryName(indexRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sibling = string.IsNullOrEmpty(parent)
            ? Path.Combine(indexRoot, "Learning")   // index root is a drive root; nest rather than go relative
            : Path.Combine(parent, "Learning");

        return (Path.GetFullPath(sibling), "sibling of index root");
    }
}
