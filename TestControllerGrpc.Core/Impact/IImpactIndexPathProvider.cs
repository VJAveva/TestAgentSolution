namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// Single source of truth for where the impact-mapping databases live on disk. Both hosts (WPF
/// <c>TestControllerGrpc</c> and <c>TestController.WebApi</c>) resolve paths through this, so the engine
/// behaves identically whichever front door is running.
/// </summary>
/// <remarks>
/// The index is a regenerable cache; the learning store is durable. They are deliberately kept in separate
/// roots so a rebuild can clear the index without destroying accumulated run outcomes.
/// </remarks>
public interface IImpactIndexPathProvider
{
    /// <summary>Directory holding the rebuildable retrieval index.</summary>
    string IndexRoot { get; }

    /// <summary>Full path to <c>impact-index.db</c> inside <see cref="IndexRoot"/>.</summary>
    string IndexFilePath { get; }

    /// <summary>Directory holding the durable learning store. Never cleared by a rebuild.</summary>
    string LearningRoot { get; }

    /// <summary>Full path to <c>impact-outcomes.db</c> inside <see cref="LearningRoot"/>.</summary>
    string OutcomeFilePath { get; }

    /// <summary>True when the index file is present (does not validate its contents).</summary>
    bool IndexExists { get; }

    /// <summary>Creates both roots when absent. Idempotent; does not throw when they already exist.</summary>
    void EnsureIndexRootExists();
}
