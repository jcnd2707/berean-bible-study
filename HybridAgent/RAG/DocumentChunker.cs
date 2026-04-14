namespace HybridAgent.Core.RAG;

/// <summary>
/// A single chunk of text ready for embedding and retrieval.
///
/// Verse metadata (BookNumber, ChapterBegin, VerseBegin, VerseEnd) is populated
/// when the chunk was produced from an e-Sword file. It is null for chunks
/// produced from plain-text sources. Both paths use the same chunker logic.
/// </summary>
public class DocumentChunk
{
    public required string Id { get; init; }   // "{source}::{chunkIndex}"
    public required string Source { get; init; }   // filename or logical name
    public required string Text { get; init; }   // plain text, ready to embed
    public required int ChunkIndex { get; init; }
    public float[] Embedding { get; set; } = [];

    // ── Verse metadata (populated only for e-Sword sourced chunks) ─────────
    public int? BookNumber { get; init; }   // e-Sword book number (1=Gen, 40=Matt…)
    public int? ChapterBegin { get; init; }
    public int? VerseBegin { get; init; }
    public int? VerseEnd { get; init; }
}

/// <summary>
/// Splits raw text into overlapping fixed-size chunks.
/// Chunking logic is unchanged — only the sources feeding it have changed.
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
        int? verseEnd = null)
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

    /// <summary>Load a plain-text file and chunk it (original behaviour, kept intact).</summary>
    public static async Task<List<DocumentChunk>> ChunkFileAsync(
        string filePath,
        int chunkSize = 500,
        int overlap = 100)
    {
        var text = await File.ReadAllTextAsync(filePath);
        var source = Path.GetFileName(filePath);
        return Chunk(text, source, chunkSize, overlap);
    }

    /// <summary>Load every .txt file in a directory and chunk them all.</summary>
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