namespace Berean.Core.Indexing;

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
        string language = "en",
        string? moduleId = null,
        string? tradition = null,
        string? idPrefix = null)
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
                    Id = $"{idPrefix ?? source}::{index}",
                    Source = source,
                    Text = slice,
                    ChunkIndex = index,
                    SourceType = sourceType,
                    Language = language,
                    ModuleId = moduleId,
                    Tradition = tradition,
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