namespace HybridAgent.Core.RAG;

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

    // ── Verse metadata (Bible and Commentary chunks only) ──────────────────
    public int? BookNumber { get; init; }
    public int? ChapterBegin { get; init; }
    public int? VerseBegin { get; init; }
    public int? VerseEnd { get; init; }
}

/// <summary>
/// Splits raw text into overlapping fixed-size chunks.
/// Chunking logic is unchanged — metadata is now richer.
/// </summary>
public static class DocumentChunker
{
    public static List<DocumentChunk> Chunk(
        string text,
        string source,
        int chunkSize = 500,
        int overlap = 100,
        int? bookNumber = null,
        int? chapter = null,
        int? verseBegin = null,
        int? verseEnd = null,
        SourceType sourceType = SourceType.Unknown,
        string language = "en")
    {
        var chunks = new List<DocumentChunk>();
        int start = 0;
        int index = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + chunkSize, text.Length);
            var slice = text[start..end].Trim();

            if (slice.Length > 0)
            {
                chunks.Add(new DocumentChunk
                {
                    Id = $"{source}::{index}",
                    Source = source,
                    Text = slice,
                    ChunkIndex = index,
                    SourceType = sourceType,
                    Language = language,
                    BookNumber = bookNumber,
                    ChapterBegin = chapter,
                    VerseBegin = verseBegin,
                    VerseEnd = verseEnd,
                });
                index++;
            }

            start += chunkSize - overlap;
        }

        return chunks;
    }

    public static async Task<List<DocumentChunk>> ChunkFileAsync(
        string filePath,
        int chunkSize = 500,
        int overlap = 100,
        SourceType sourceType = SourceType.PlainText,
        string language = "en")
    {
        var text = await File.ReadAllTextAsync(filePath);
        var source = Path.GetFileName(filePath);
        return Chunk(text, source, chunkSize, overlap,
            sourceType: sourceType, language: language);
    }

    public static async Task<List<DocumentChunk>> ChunkDirectoryAsync(
        string directory,
        string searchPattern = "*.txt",
        int chunkSize = 500,
        int overlap = 100)
    {
        var all = new List<DocumentChunk>();
        var files = Directory.GetFiles(directory, searchPattern, SearchOption.AllDirectories);
        foreach (var file in files)
            all.AddRange(await ChunkFileAsync(file, chunkSize, overlap));
        return all;
    }
}