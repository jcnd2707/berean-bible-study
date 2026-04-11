namespace HybridAgent.RAG;

/// <summary>
/// Represents a single chunk of text extracted from a source document.
/// </summary>
public class DocumentChunk
{
    public required string Id { get; init; }   // unique: "source::chunkIndex"
    public required string Source { get; init; }   // file path or logical name
    public required string Text { get; init; }   // the raw chunk text
    public required int ChunkIndex { get; init; }
    public float[] Embedding { get; set; } = [];
}

/// <summary>
/// Splits raw text into overlapping fixed-size chunks so that context
/// at chunk boundaries isn't lost during retrieval.
/// </summary>
public static class DocumentChunker
{
    /// <summary>
    /// Split a single string into chunks.
    /// </summary>
    /// <param name="text">Full document text.</param>
    /// <param name="source">Logical name shown in citations (e.g. filename).</param>
    /// <param name="chunkSize">Target character count per chunk.</param>
    /// <param name="overlap">Characters of overlap between adjacent chunks.</param>
    public static List<DocumentChunk> Chunk(
        string text,
        string source,
        int chunkSize = 500,
        int overlap = 100)
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
                });
                index++;
            }

            start += chunkSize - overlap;
        }

        return chunks;
    }

    /// <summary>
    /// Load a plain-text file from disk and chunk it.
    /// </summary>
    public static async Task<List<DocumentChunk>> ChunkFileAsync(
        string filePath,
        int chunkSize = 500,
        int overlap = 100)
    {
        var text = await File.ReadAllTextAsync(filePath);
        var source = Path.GetFileName(filePath);
        return Chunk(text, source, chunkSize, overlap);
    }

    /// <summary>
    /// Load every .txt file in a directory and chunk them all.
    /// </summary>
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