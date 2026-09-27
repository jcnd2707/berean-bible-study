namespace Berean.Core.Retrieval;

/// <summary>
/// Classifies what kind of content a chunk came from.
/// Stored in the Chunks table so retrieval can filter by type.
/// </summary>
public enum SourceType
{
    Unknown = 0,
    Bible = 1,   // .bblx
    Commentary = 2,   // .cmtx
    Dictionary = 3,   // .dctx / .lexx
    Topic = 4,   // .topx / .devx / .refx
    PlainText = 5,   // .txt / .md / .cs etc.
    Book = 6,    // prose books from BereanResource.Api /api/books
}

/// <summary>
/// A single chunk of text ready for embedding and retrieval.
/// </summary>
public class DocumentChunk
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Text { get; init; }
    public required int ChunkIndex { get; init; }
    public float[] Embedding { get; set; } = [];

    // ── Classification metadata ────────────────────────────────────────────
    public SourceType SourceType { get; init; } = SourceType.Unknown;
    public string Language { get; init; } = "en";   // "en" | "es" | …

    /// <summary>Module the chunk came from (commentary/book module id). Null for legacy rows.</summary>
    public string? ModuleId { get; set; }

    /// <summary>One of <see cref="Traditions"/>. Null for rows not yet backfilled.</summary>
    public string? Tradition { get; set; }

    /// <summary>Not stored: a display location for chunks built on the fly ("John 3:16", or a dictionary headword).</summary>
    public string? Locator { get; init; }

    // ── Verse metadata (Bible and Commentary chunks only) ──────────────────
    public int? BookNumber { get; init; }
    public int? ChapterBegin { get; init; }
    public int? VerseBegin { get; init; }
    public int? VerseEnd { get; init; }
}
