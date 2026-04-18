using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace HybridAgent.Core.RAG;

/// <summary>
/// SQLite-backed vector store with in-memory cosine search and MMR reranking.
///
/// Schema v2 adds SourceType (int) and Language (text) columns so retrieval
/// can filter by content type and language without separate databases.
///
/// On startup, InitialiseAsync() runs ALTER TABLE to add the new columns to
/// existing databases — no manual migration required.
/// </summary>
public class SqliteVectorStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly List<DocumentChunk> _cache = [];

    // Guards _cache against concurrent reads (background indexing) and writes (query handlers).
    // Lock is never held across await boundaries — only around synchronous cache mutations/reads.
    private readonly ReaderWriterLockSlim _cacheLock = new(LockRecursionPolicy.NoRecursion);

    public int Count
    {
        get
        {
            _cacheLock.EnterReadLock();
            try { return _cache.Count; }
            finally { _cacheLock.ExitReadLock(); }
        }
    }

    public SqliteVectorStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    // ── Schema init ────────────────────────────────────────────────────────

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);

        // Create table with full v2 schema
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
                VerseEnd     INTEGER,
                SourceType   INTEGER NOT NULL DEFAULT 0,
                Language     TEXT    NOT NULL DEFAULT 'en'
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_Verse
                ON Chunks (BookNumber, ChapterBegin, VerseBegin, VerseEnd)
                WHERE BookNumber IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_Chunks_TypeLang
                ON Chunks (SourceType, Language);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Migrate existing databases that have the old schema (no SourceType/Language)
        await AddColumnIfMissingAsync(conn, "SourceType", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(conn, "Language", "TEXT    NOT NULL DEFAULT 'en'", ct);
    }

    // ── Write ──────────────────────────────────────────────────────────────

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
                 BookNumber, ChapterBegin, VerseBegin, VerseEnd,
                 SourceType, Language)
            VALUES
                (@id, @source, @idx, @text, @emb,
                 @book, @chapter, @vb, @ve,
                 @st, @lang)
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
        var pSt = cmd.Parameters.Add("@st", SqliteType.Integer);
        var pLang = cmd.Parameters.Add("@lang", SqliteType.Text);

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
            pSt.Value = (int)chunk.SourceType;
            pLang.Value = chunk.Language;

            await cmd.ExecuteNonQueryAsync(ct);

            // Write lock held only for the synchronous cache mutation, never across awaits
            _cacheLock.EnterWriteLock();
            try { _cache.Add(chunk); }
            finally { _cacheLock.ExitWriteLock(); }
        }

        await tx.CommitAsync(ct);
    }

    public Task SaveAsync(string? _ = null) => Task.CompletedTask;

    // ── Read ───────────────────────────────────────────────────────────────

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return false;

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Source, ChunkIndex, Text, Embedding, " +
            "       BookNumber, ChapterBegin, VerseBegin, VerseEnd, " +
            "       SourceType, Language " +
            "FROM Chunks";

        // Build new list without holding any lock across async reads
        var loaded = new List<DocumentChunk>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blob = (byte[])reader["Embedding"];
            loaded.Add(new DocumentChunk
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
                SourceType = reader.IsDBNull(9) ? SourceType.Unknown
                                                   : (SourceType)reader.GetInt32(9),
                Language = reader.IsDBNull(10) ? "en" : reader.GetString(10),
            });
        }

        // Swap into cache atomically
        _cacheLock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _cache.AddRange(loaded);
        }
        finally { _cacheLock.ExitWriteLock(); }

        return _cache.Count > 0;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        _cacheLock.EnterWriteLock();
        try { _cache.Clear(); }
        finally { _cacheLock.ExitWriteLock(); }

        if (!File.Exists(_dbPath)) return;

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Chunks";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Semantic search with MMR ───────────────────────────────────────────

    /// <summary>
    /// MMR search with optional source-type and language filters.
    /// Pass null filters to search all chunks (default behaviour).
    /// </summary>
    public List<DocumentChunk> Search(
        float[] queryEmbedding,
        int topK = 8,
        float lambda = 0.6f,
        int candidateK = 80,
        SourceType? sourceType = null,
        string? language = null)
    {
        // Snapshot under read lock — LINQ runs on the snapshot, not the live list
        List<DocumentChunk> snapshot;
        _cacheLock.EnterReadLock();
        try
        {
            if (_cache.Count == 0) return [];
            snapshot = [.. _cache];
        }
        finally { _cacheLock.ExitReadLock(); }

        var pool = snapshot.AsEnumerable();
        if (sourceType.HasValue) pool = pool.Where(c => c.SourceType == sourceType.Value);
        if (language is not null) pool = pool.Where(c => c.Language == language);

        var candidates = pool
            .Select(c => (chunk: c, score: CosineSimilarity(queryEmbedding, c.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(candidateK)
            .ToList();

        if (candidates.Count == 0) return [];

        // MMR greedy selection
        var selected = new List<(DocumentChunk chunk, float score)>();
        var remaining = candidates.ToList();

        while (selected.Count < topK && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmr = float.MinValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                var relevance = remaining[i].score;
                var maxSim = selected.Count == 0
                    ? 0f
                    : selected.Max(s =>
                        CosineSimilarity(remaining[i].chunk.Embedding, s.chunk.Embedding));

                var mmr = lambda * relevance - (1f - lambda) * maxSim;
                if (mmr > bestMmr) { bestMmr = mmr; bestIdx = i; }
            }

            selected.Add(remaining[bestIdx]);
            remaining.RemoveAt(bestIdx);
        }

        return selected.Select(x => x.chunk).ToList();
    }

    // ── Verse-pinned retrieval ─────────────────────────────────────────────

    /// <summary>
    /// Returns chunks covering a specific verse, optionally filtered by type and language.
    /// </summary>
    public List<DocumentChunk> SearchByVerse(
        int bookNumber,
        int chapter,
        int verse,
        SourceType? sourceType = null,
        string? language = null)
    {
        List<DocumentChunk> snapshot;
        _cacheLock.EnterReadLock();
        try { snapshot = [.. _cache]; }
        finally { _cacheLock.ExitReadLock(); }

        var q = snapshot.Where(c =>
            c.BookNumber == bookNumber &&
            c.ChapterBegin == chapter &&
            c.VerseBegin <= verse &&
            c.VerseEnd >= verse);

        if (sourceType.HasValue) q = q.Where(c => c.SourceType == sourceType.Value);
        if (language is not null) q = q.Where(c => c.Language == language);

        return q.ToList();
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

    // ── Migration helper ───────────────────────────────────────────────────

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string column, string definition, CancellationToken ct)
    {
        var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Chunks') WHERE name='{column}'";
        var exists = (long)(await check.ExecuteScalarAsync(ct))! > 0;
        if (exists) return;

        var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE Chunks ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(ct);
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

    public ValueTask DisposeAsync()
    {
        _cacheLock.Dispose();
        return ValueTask.CompletedTask;
    }
}