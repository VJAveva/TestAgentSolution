using Microsoft.EntityFrameworkCore;

namespace TestControllerGrpc.Core.Impact.Learning;

/// <summary>One completed impact-mapping run (P20).</summary>
public sealed class MappingRun
{
    public Guid Id { get; set; }
    public string AreaId { get; set; } = string.Empty;
    public int? PullRequestId { get; set; }
    public SelectionTier Tier { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public string ScoringMode { get; set; } = string.Empty;
    public int SelectedCount { get; set; }
    public double BudgetUsedSeconds { get; set; }
    public bool EarlyExit { get; set; }
}

/// <summary>One test case selected by a run, with the score and provenance that put it there (P20).</summary>
public sealed class MappingSelection
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public int TestCaseId { get; set; }
    public int FeatureId { get; set; }
    public double FinalScore { get; set; }
    public int Grade { get; set; }
    public AnchorSource? AnchorSource { get; set; }
    public int Rank { get; set; }
    public bool WasExecuted { get; set; }
}

/// <summary>The observed result of executing a selected test case (P20).</summary>
public sealed class ExecutionOutcome
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public int TestCaseId { get; set; }
    public string Result { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string? FailureSignature { get; set; }
}

/// <summary>A field escape: a test case that would have caught a bug but was not selected (P20).</summary>
public sealed class EscapeRecord
{
    public Guid Id { get; set; }
    public string AreaId { get; set; } = string.Empty;
    public int TestCaseId { get; set; }
    public DateTimeOffset DetectedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

/// <summary>
/// EF Core context for the durable outcome/learning store (P20). Deliberately a SEPARATE database from the
/// rebuildable retrieval index — a forced index rebuild must never wipe accumulated learning history. Created
/// via EnsureCreated (no migrations); the data is append-mostly and versioned by <see cref="MappingRun.ModelVersion"/>.
/// </summary>
public sealed class OutcomeDbContext : DbContext
{
    /// <summary>Creates the context with externally supplied options (factory or test-owned connection).</summary>
    public OutcomeDbContext(DbContextOptions<OutcomeDbContext> options) : base(options)
    {
    }

    public DbSet<MappingRun> MappingRuns => Set<MappingRun>();

    public DbSet<MappingSelection> MappingSelections => Set<MappingSelection>();

    public DbSet<ExecutionOutcome> ExecutionOutcomes => Set<ExecutionOutcome>();

    public DbSet<EscapeRecord> EscapeRecords => Set<EscapeRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MappingRun>(entity =>
        {
            entity.ToTable("MappingRuns");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.AreaId, r.CreatedUtc });
        });

        modelBuilder.Entity<MappingSelection>(entity =>
        {
            entity.ToTable("MappingSelections");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.RunId);
            entity.HasIndex(s => s.TestCaseId);
        });

        modelBuilder.Entity<ExecutionOutcome>(entity =>
        {
            entity.ToTable("ExecutionOutcomes");
            entity.HasKey(o => o.Id);
            entity.HasIndex(o => new { o.RunId, o.TestCaseId });
            entity.HasIndex(o => o.TestCaseId);
        });

        modelBuilder.Entity<EscapeRecord>(entity =>
        {
            entity.ToTable("EscapeRecords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AreaId);
        });
    }
}
