namespace TestControllerGrpc.Core.Impact.Index;

// EF Core persistence entities for the retrieval index (P06). These are mutable sealed classes rather
// than records — EF Core change-tracking needs settable properties and a parameterless constructor.
// The database is a rebuildable, host-local cache; it holds no durable business state.

/// <summary>A single indexed corpus document — a flattened ADO Feature or Test Case.</summary>
public sealed class IndexedDocument
{
    /// <summary>Stable document id, e.g. "F:1234" (Feature) or "TC:5678" (Test Case).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether this document is a Feature or a Test Case.</summary>
    public IndexKind Kind { get; set; }

    /// <summary>The underlying ADO work item id.</summary>
    public int WorkItemId { get; set; }

    /// <summary>Short human-readable title (work item title).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Flattened searchable text (title + description + steps, etc.).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Content hash used to skip re-indexing unchanged work items during incremental builds.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>ADO System.Rev captured at index time.</summary>
    public int Revision { get; set; }

    /// <summary>Document length in tokens, used for BM25 length normalisation.</summary>
    public int Length { get; set; }

    /// <summary>For a feature, its linked test-case count (drives fan-out damping); 0 for test cases.</summary>
    public int ChildCount { get; set; }

    /// <summary>When this document was last (re)indexed.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>A term posting: how often <see cref="Term"/> occurs in document <see cref="DocumentId"/>.</summary>
public sealed class DocumentTerm
{
    /// <summary>Owning document id (<see cref="IndexedDocument.Id"/>).</summary>
    public string DocumentId { get; set; } = string.Empty;

    /// <summary>Normalised term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Occurrences of the term within the document.</summary>
    public int TermFrequency { get; set; }
}

/// <summary>Corpus-wide document frequency for a term. IDF is always computed over the ENTIRE corpus,
/// never over a retrieved subset, so this table is the single source of truth for term rarity.</summary>
public sealed class CorpusStatistic
{
    /// <summary>Normalised term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Number of documents in the corpus that contain the term.</summary>
    public int DocumentFrequency { get; set; }
}

/// <summary>An embedding vector for a document, stored as a packed little-endian BLOB (see <see cref="VectorBlob"/>).</summary>
public sealed class DocumentVector
{
    /// <summary>Owning document id (<see cref="IndexedDocument.Id"/>).</summary>
    public string DocumentId { get; set; } = string.Empty;

    /// <summary>Packed float vector (4 bytes per dimension).</summary>
    public byte[] Vector { get; set; } = Array.Empty<byte>();

    /// <summary>Number of dimensions (length of the unpacked vector).</summary>
    public int Dimension { get; set; }

    /// <summary>Embedding model identifier the vector was produced with (guards against mixing models).</summary>
    public string Model { get; set; } = string.Empty;
}

/// <summary>Key/value store for corpus-level scalars: document count, average length, tokenizer/embedding
/// versions and build timestamps. Kept as a table so the index is self-describing after a rebuild.</summary>
public sealed class IndexMetadata
{
    /// <summary>Metadata key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Metadata value (stringified).</summary>
    public string Value { get; set; } = string.Empty;
}
