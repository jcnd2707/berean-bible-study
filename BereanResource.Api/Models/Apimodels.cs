namespace BereanResourceApi.Models;

// ── Resource discovery ────────────────────────────────────────────────────────

public record ResourceModule(
    string ModuleId,        // filename without extension, e.g. "MKJV"
    string Name,            // human-readable name from file metadata
    string Language,        // "en", "es", etc.
    string FilePath
);

// ── Bible ─────────────────────────────────────────────────────────────────────

public record BookInfo(
    int Number,             // internal e-Sword book number (1–66)
    string Name,            // "Genesis", "Génesis", etc.
    string Abbreviation,    // "Gen", "Gn", etc.
    int ChapterCount
);

public record VerseRecord(
    int Book,
    int Chapter,
    int Verse,
    string BookName,
    string Reference,       // "Genesis 1:1"
    string Text
);

public record ChapterRecord(
    int Book,
    string BookName,
    int Chapter,
    string ModuleId,
    List<VerseRecord> Verses
);

// ── Commentary ────────────────────────────────────────────────────────────────

public record CommentaryEntry(
    int Book,
    string BookName,
    int Chapter,
    int VerseBegin,
    int VerseEnd,
    string Reference,
    string? Marker,
    string Text
);

public record CommentaryChapter(
    string ModuleId,
    int Book,
    string BookName,
    int Chapter,
    List<CommentaryEntry> Entries
);

// ── Dictionary / Lexicon ──────────────────────────────────────────────────────

public record DictionaryEntry(
    string Topic,
    string Definition
);

// ── Notes ─────────────────────────────────────────────────────────────────────

public record NoteRecord(
    string Reference,       // "John 3:16"
    string Text,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record UpsertNoteRequest(
    string Text
);

// ── Cross References ──────────────────────────────────────────────────────────

public record CrossReferenceEntry(
    string FromReference,
    string ToReference,
    int ToBook,
    int ToChapter,
    int ToVerseStart,
    int ToVerseEnd,
    int Votes              // relevance score from openbible.info — higher = stronger link
);

public record CrossReferenceResult(
    string Reference,
    List<CrossReferenceEntry> References
);