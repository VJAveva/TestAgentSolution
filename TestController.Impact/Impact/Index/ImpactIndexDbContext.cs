using Microsoft.EntityFrameworkCore;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// EF Core context over the SQLite retrieval index (P06): documents, term postings, corpus statistics,
/// embedding vectors and metadata. The database is a rebuildable, host-local cache created via
/// <see cref="ImpactIndexInitializer"/> (EnsureCreated + WAL), never through migrations. Singleton
/// services obtain instances through <c>IDbContextFactory&lt;ImpactIndexDbContext&gt;</c> (wired in P25).
/// </summary>
public sealed class ImpactIndexDbContext : DbContext
{
    /// <summary>Creates the context with externally supplied options (factory or test-owned connection).</summary>
    public ImpactIndexDbContext(DbContextOptions<ImpactIndexDbContext> options) : base(options)
    {
    }

    /// <summary>Indexed corpus documents (Features and Test Cases).</summary>
    public DbSet<IndexedDocument> Documents => Set<IndexedDocument>();

    /// <summary>Per-document term postings.</summary>
    public DbSet<DocumentTerm> DocumentTerms => Set<DocumentTerm>();

    /// <summary>Corpus-wide document frequencies (source of truth for IDF).</summary>
    public DbSet<CorpusStatistic> CorpusStatistics => Set<CorpusStatistic>();

    /// <summary>Per-document embedding vectors.</summary>
    public DbSet<DocumentVector> DocumentVectors => Set<DocumentVector>();

    /// <summary>Corpus-level scalar metadata.</summary>
    public DbSet<IndexMetadata> Metadata => Set<IndexMetadata>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IndexedDocument>(entity =>
        {
            entity.ToTable("IndexedDocuments");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).HasMaxLength(64);
            entity.HasIndex(d => d.Kind);
            entity.HasIndex(d => d.WorkItemId);
        });

        modelBuilder.Entity<DocumentTerm>(entity =>
        {
            entity.ToTable("DocumentTerms");
            entity.HasKey(t => new { t.DocumentId, t.Term });
            entity.HasIndex(t => t.Term); // postings lookup by term during scoring
        });

        modelBuilder.Entity<CorpusStatistic>(entity =>
        {
            entity.ToTable("CorpusStatistics");
            entity.HasKey(c => c.Term);
        });

        modelBuilder.Entity<DocumentVector>(entity =>
        {
            entity.ToTable("DocumentVectors");
            entity.HasKey(v => v.DocumentId);
        });

        modelBuilder.Entity<IndexMetadata>(entity =>
        {
            entity.ToTable("IndexMetadata");
            entity.HasKey(m => m.Key);
        });
    }
}
