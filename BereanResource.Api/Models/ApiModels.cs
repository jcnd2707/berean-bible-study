namespace BereanResourceApi.Models;

// ── Resource discovery ────────────────────────────────────────────────────────

public record ResourceModule(
    string ModuleId,        // filename without extension, e.g. "MKJV"
    string Name,            // human-readable name from file metadata
    string Language,        // "en", "es", etc.
    string FilePath,
    string? Tradition = null,   // from ModuleProfiles config; "Unclassified" when no profile
    string? Era = null,
    string? DisplayName = null  // short citation name from ModuleProfiles, falls back to Name
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
    string Reference,
    string Text,
    List<StrongsWord>? StrongsWords = null
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

// ── Reading progress ──────────────────────────────────────────────────────────

public record ReadChapterRecord(
    int Book,               // canonical 1-66, not a module-specific abbreviation
    int Chapter,
    DateTime ReadAt
);

// ── Profiles ──────────────────────────────────────────────────────────────────

public record ProfileRecord(
    string Id,
    string Name,
    string? Color,
    DateTime CreatedAt
);

public record CreateProfileRequest(
    string Name,
    string? Color = null
);

public record RenameProfileRequest(
    string Name
);

// ── Strong's occurrences ──────────────────────────────────────────────────────

public record StrongsBookCount(int Book, string BookName, int Count);

public record StrongsSample(string Reference, string Word, string Text);

public record StrongsOccurrences(
    string Number,
    int Count,
    List<StrongsBookCount> ByBook,
    List<StrongsSample> Sample
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

/// <summary>
/// Metadata from the <c>details</c> table present in MySword .dct files.
/// </summary>
public record ModuleDetails(
    string Title,
    string Abbreviation,
    string Description,
    string Author,
    string Version,
    string? VersionDate,
    string Publisher,
    bool IsStrongs,
    bool RightToLeft
);

/// <summary>
/// Module-level metadata read from the translations table.
/// Title and License are optional — older or non-standard modules may omit them.
/// </summary>
public record TranslationInfo(
    string Translation,
    string? Title,
    string? License,
    bool HasStrongs
);

public record StrongsWord(
    string Word,
    string Number
);

/// <summary>
/// Identifies the on-disk format of a Bible module.
/// Scrollmapper: one .db per translation, prefixed table names, translations table.
/// MySword:      one .bbl per translation, generic Bible/Details tables.
/// </summary>
public enum BibleFormat
{
    Scrollmapper,
    MySword
}

// ── Books ─────────────────────────────────────────────────────────────────────

public record BookSummary(
    string ModuleId,
    string Title,
    string Author,
    string Publisher,
    string Language,
    string? Tradition = null,
    string? Era = null
);

public record BookMeta(
    string ModuleId,
    string Title,
    string Author,
    string Publisher,
    string Language,
    string? Isbn,
    string? Rights,
    DateTime ImportedAt
);

public record BookChapterSummary(
    int Id,
    int ChapterNumber,
    string Title,
    int OrderIndex
);

public record BookParagraph(
    int OrderIndex,
    string CssClass,
    string Content,
    string PlainText
);

public record BookChapterContent(
    string ModuleId,
    int ChapterId,
    int ChapterNumber,
    string ChapterTitle,
    List<BookParagraph> Paragraphs
);

public record BookSearchResult(
    string ChapterTitle,
    int ChapterNumber,
    int ParagraphIndex,
    string CssClass,
    string Content,
    string PlainText
);