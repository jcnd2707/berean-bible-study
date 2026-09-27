namespace Berean.Core.Resources;

// ── Response DTOs (mirror BereanResource.Api JSON shapes) ────────────────────

public record ApiResourceModule(string ModuleId, string Name, string Language, string FilePath,
    string? Tradition = null, string? Era = null, string? DisplayName = null);
public record ApiBookSummary(string ModuleId, string Title, string Author, string Publisher, string Language,
    string? Tradition = null, string? Era = null);
public record ApiBibleBook(int Number, string Name, string Abbreviation, int ChapterCount);
public record ApiVerseRecord(int Book, int Chapter, int Verse, string BookName, string Reference, string Text);
public record ApiChapterRecord(int Book, string BookName, int Chapter, string ModuleId, List<ApiVerseRecord> Verses);
public record ApiCommentaryEntry(int Book, string BookName, int Chapter, int VerseBegin, int VerseEnd, string Reference, string? Marker, string Text);
public record ApiCommentaryChapter(string ModuleId, int Book, string BookName, int Chapter, List<ApiCommentaryEntry> Entries);
public record ApiDictionaryEntry(string Topic, string Definition);
public record ApiBookChapterSummary(int Id, int ChapterNumber, string Title, int OrderIndex);
public record ApiBookParagraph(int OrderIndex, string CssClass, string Content, string PlainText);
public record ApiCrossReferenceEntry(string FromReference, string ToReference, int ToBook, int ToChapter, int ToVerseStart, int ToVerseEnd, int Votes);
public record ApiCrossReferenceResult(string Reference, List<ApiCrossReferenceEntry> References);
public record ApiBookChapterContent(string ModuleId, int ChapterId, int ChapterNumber, string ChapterTitle, List<ApiBookParagraph> Paragraphs);

public record ApiStrongsBookCount(int Book, string BookName, int Count);
public record ApiStrongsSample(string Reference, string Word, string Text);
public record ApiStrongsOccurrences(string Number, int Count, List<ApiStrongsBookCount> ByBook, List<ApiStrongsSample> Sample);
