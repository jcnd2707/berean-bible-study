using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Replaces VectorStore (index.json) with a SQLite database (rag.db).
///
/// Public API is identical to VectorStore so RagPipeline needs minimal changes:
///   Add()      → INSERT OR IGNORE into Chunks
///   SaveAsync() → no-op (writes happen immediately in Add)
///   LoadAsync() → SELECT all rows, deserialize embeddings to float[]
///   Search()   → in-memory cosine similarity (same as before)
///
/// Embeddings are stored as BLOB (raw IEEE-754 float bytes, 768 floats × 4 bytes = 3072 bytes).
/// This is more compact and faster to deserialize than JSON.
///
/// Verse-pinned retrieval: SearchByVerse(bookNumber, chapter, verse)
/// returns all chunks whose stored verse range covers that exact verse.
/// </summary>
public class SqliteVectorStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly List<DocumentChunk> _cache = [];  // in-memory after Load

    public int Count => _cache.Count;

    public SqliteVectorStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    // ── Schema init ────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure the Chunks table and its indexes exist.
    /// Call once at startup before Add() or LoadAsync().
    /// </summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Chunks (
                Id           TEXT    NOT NULL PRIMARY KEY,
                Source       TEXT    NOT NULL,
                ChunkIndex   INTEGER NOT NULL,
                Text         TEXT    NOT NULL,
                Embedding    BLOB    NOT NULL,
                BookNumber   INTEGER,
                ChapterBegin INTEGER,
                VerseBegin   INTEGER,
                VerseEnd     INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_Verse
                ON Chunks (BookNumber, ChapterBegin, VerseBegin, VerseEnd)
                WHERE BookNumber IS NOT NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Write ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Persist chunks to SQLite and add them to the in-memory cache.
    /// Uses INSERT OR IGNORE so re-indexing the same source is safe.
    /// </summary>
    public async Task AddAsync(
        IEnumerable<DocumentChunk> chunks,
        CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Chunks
                (Id, Source, ChunkIndex, Text, Embedding,
                 BookNumber, ChapterBegin, VerseBegin, VerseEnd)
            VALUES
                (@id, @source, @idx, @text, @emb,
                 @book, @chapter, @vb, @ve)
            """;

        var pId = cmd.Parameters.Add("@id", SqliteType.Text);
        var pSource = cmd.Parameters.Add("@source", SqliteType.Text);
        var pIdx = cmd.Parameters.Add("@idx", SqliteType.Integer);
        var pText = cmd.Parameters.Add("@text", SqliteType.Text);
        var pEmb = cmd.Parameters.Add("@emb", SqliteType.Blob);
        var pBook = cmd.Parameters.Add("@book", SqliteType.Integer);
        var pChapter = cmd.Parameters.Add("@chapter", SqliteType.Integer);
        var pVb = cmd.Parameters.Add("@vb", SqliteType.Integer);
        var pVe = cmd.Parameters.Add("@ve", SqliteType.Integer);

        foreach (var chunk in chunks)
        {
            pId.Value = chunk.Id;
            pSource.Value = chunk.Source;
            pIdx.Value = chunk.ChunkIndex;
            pText.Value = chunk.Text;
            pEmb.Value = FloatsToBytes(chunk.Embedding);
            pBook.Value = chunk.BookNumber is int b ? b : DBNull.Value;
            pChapter.Value = chunk.ChapterBegin is int c ? c : DBNull.Value;
            pVb.Value = chunk.VerseBegin is int v ? v : DBNull.Value;
            pVe.Value = chunk.VerseEnd is int e ? e : DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct);
            _cache.Add(chunk);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>No-op — kept for API compatibility with VectorStore.</summary>
    public Task SaveAsync(string? _ = null) => Task.CompletedTask;

    // ── Read ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Load all chunks from rag.db into memory. Returns false if the file
    /// does not exist or is empty (caller should then re-index).
    /// </summary>
    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return false;

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Source, ChunkIndex, Text, Embedding, " +
            "       BookNumber, ChapterBegin, VerseBegin, VerseEnd " +
            "FROM Chunks";

        _cache.Clear();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blob = (byte[])reader["Embedding"];
            _cache.Add(new DocumentChunk
            {
                Id = reader.GetString(0),
                Source = reader.GetString(1),
                ChunkIndex = reader.GetInt32(2),
                Text = reader.GetString(3),
                Embedding = BytesToFloats(blob),
                BookNumber = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ChapterBegin = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                VerseBegin = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                VerseEnd = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            });
        }

        return _cache.Count > 0;
    }

    /// <summary>Clear both the in-memory cache and the on-disk database.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        if (!File.Exists(_dbPath)) return;

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Chunks";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Semantic search ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the top-k chunks most similar to the query embedding.
    /// Cosine similarity is computed in-memory — identical to VectorStore.
    /// </summary>
    public List<DocumentChunk> Search(float[] queryEmbedding, int topK = 5)
    {
        if (_cache.Count == 0) return [];

        return _cache
            .Select(c => (chunk: c, score: CosineSimilarity(queryEmbedding, c.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.chunk)
            .ToList();
    }

    // ── Verse-pinned retrieval ─────────────────────────────────────────────

    /// <summary>
    /// Returns all chunks whose stored verse range covers the requested verse.
    /// Hits the in-memory cache for speed — no extra DB round-trip needed.
    /// </summary>
    public List<DocumentChunk> SearchByVerse(int bookNumber, int chapter, int verse)
    {
        return _cache
            .Where(c =>
                c.BookNumber == bookNumber &&
                c.ChapterBegin == chapter &&
                c.VerseBegin <= verse &&
                c.VerseEnd >= verse)
            .ToList();
    }

    // ── Math ───────────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0f : dot / denom;
    }

    // ── Serialization ──────────────────────────────────────────────────────

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(floats).CopyTo(bytes);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return floats;
    }

    private SqliteConnection OpenConnection() =>
        new($"Data Source={_dbPath};");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}