using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Berean.Core.Retrieval;

/// <summary>
/// SQLite-backed vector store with in-memory cosine search and MMR reranking.
///
/// Schema v2 added SourceType (int) and Language (text); v3 adds ModuleId and Tradition so
/// retrieval can filter by tradition and cap how much any one module contributes.
///
/// On startup, InitialiseAsync() runs ALTER TABLE to add new columns to existing databases,
/// after copying the file once as a safety net — no manual migration required.
/// </summary>
public class SqliteVectorStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly List<DocumentChunk> _cache = [];
    private readonly ILogger<SqliteVectorStore> _log;

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

    /// <summary>True when this run created the IndexedModules table (an index built before markers existed).</summary>
    public bool CompletionMarkersCreated { get; private set; }

    public SqliteVectorStore(string dbPath, ILogger<SqliteVectorStore> log)
    {
        _dbPath = dbPath;
        _log = log;
    }

    // ── Schema init ────────────────────────────────────────────────────────

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);

        BackupBeforeMigration();

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
                VerseEnd     INTEGER,
                SourceType   INTEGER NOT NULL DEFAULT 0,
                Language     TEXT    NOT NULL DEFAULT 'en',
                ModuleId     TEXT,
                Tradition    TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_Verse
                ON Chunks (BookNumber, ChapterBegin, VerseBegin, VerseEnd)
                WHERE BookNumber IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_Chunks_TypeLang
                ON Chunks (SourceType, Language);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await AddColumnIfMissingAsync(conn, "SourceType", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(conn, "Language", "TEXT    NOT NULL DEFAULT 'en'", ct);
        await AddColumnIfMissingAsync(conn, "ModuleId", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "Tradition", "TEXT", ct);

        // Which modules finished indexing. A module with rows but no marker was interrupted
        // (for example the session disconnected) and is re-indexed from scratch.
        var markerCheck = conn.CreateCommand();
        markerCheck.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='IndexedModules'";
        CompletionMarkersCreated = (long)(await markerCheck.ExecuteScalarAsync(ct))! == 0;
        if (CompletionMarkersCreated)
        {
            var markers = conn.CreateCommand();
            markers.CommandText = """
                CREATE TABLE IndexedModules (
                    ModuleId    TEXT    NOT NULL,
                    SourceType  INTEGER NOT NULL,
                    CompletedAt TEXT    NOT NULL,
                    PRIMARY KEY (ModuleId, SourceType)
                )
                """;
            await markers.ExecuteNonQueryAsync(ct);
        }

        // Must come after the ALTERs: on an old database the column doesn't exist until now.
        var index = conn.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_Chunks_Tradition ON Chunks (Tradition)";
        await index.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Copies an existing database aside once before the tradition columns are added.
    /// The migration only adds columns and tags rows, but the index took hours to embed.
    /// </summary>
    private void BackupBeforeMigration()
    {
        if (!File.Exists(_dbPath)) return;

        try
        {
            using var conn = OpenConnection();
            conn.Open();
            var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Chunks'";
            if ((long)check.ExecuteScalar()! == 0) return;

            var hasTradition = conn.CreateCommand();
            hasTradition.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Chunks') WHERE name='Tradition'";
            if ((long)hasTradition.ExecuteScalar()! > 0) return;
        }
        catch (SqliteException) { return; }

        var backup = _dbPath + ".pre-tradition.bak";
        if (File.Exists(backup)) return;

        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, backup);
        _log.LogInformation("[Store] Backed up index before adding tradition columns: {Backup}", backup);
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
                 SourceType, Language, ModuleId, Tradition)
            VALUES
                (@id, @source, @idx, @text, @emb,
                 @book, @chapter, @vb, @ve,
                 @st, @lang, @module, @tradition)
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
        var pModule = cmd.Parameters.Add("@module", SqliteType.Text);
        var pTradition = cmd.Parameters.Add("@tradition", SqliteType.Text);

        var added = new List<DocumentChunk>();

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
            pModule.Value = chunk.ModuleId is null ? DBNull.Value : chunk.ModuleId;
            pTradition.Value = chunk.Tradition is null ? DBNull.Value : chunk.Tradition;

            await cmd.ExecuteNonQueryAsync(ct);
            added.Add(chunk);
        }

        await tx.CommitAsync(ct);

        // Only cache what was committed, so a failed batch never leaves phantom chunks in memory.
        _cacheLock.EnterWriteLock();
        try { _cache.AddRange(added); }
        finally { _cacheLock.ExitWriteLock(); }
    }

    public Task SaveAsync(string? _ = null) => Task.CompletedTask;

    // ── Backfill ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tags rows with ModuleId and Tradition without re-embedding: the embeddings don't change,
    /// only the metadata does. Also re-tags a module whose profile changed since it was indexed.
    /// Run before <see cref="LoadAsync"/> so the cache reads the tagged rows.
    ///
    ///   Commentary rows:  Source is the module id.
    ///   Book rows:        Source is "&lt;Title&gt;, Chapter N — …" (matched by exact prefix, not LIKE,
    ///                     so a % or _ in a title can't act as a wildcard).
    /// Rows from the older file mode use file names as Source and stay untagged (Unclassified).
    /// </summary>
    public async Task<BackfillReport> BackfillTraditionsAsync(
        ModuleCatalog catalog, CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath)) return new BackfillReport(0, new Dictionary<string, long>(), []);

        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long updated = 0;

        foreach (var m in catalog.Commentaries)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                UPDATE Chunks SET ModuleId = @m, Tradition = @t
                WHERE SourceType = @st AND Source = @m
                  AND (ModuleId IS NULL OR Tradition IS NULL OR Tradition <> @t)
                """;
            cmd.Parameters.AddWithValue("@m", m.ModuleId);
            cmd.Parameters.AddWithValue("@t", m.Tradition);
            cmd.Parameters.AddWithValue("@st", (int)SourceType.Commentary);
            updated += await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var (title, module) in catalog.BookTitles)
        {
            var prefix = title + ", Chapter ";
            var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                UPDATE Chunks SET ModuleId = @m, Tradition = @t
                WHERE SourceType = @st AND substr(Source, 1, length(@p)) = @p
                  AND (ModuleId IS NULL OR Tradition IS NULL OR Tradition <> @t)
                """;
            cmd.Parameters.AddWithValue("@m", module.ModuleId);
            cmd.Parameters.AddWithValue("@t", module.Tradition);
            cmd.Parameters.AddWithValue("@st", (int)SourceType.Book);
            cmd.Parameters.AddWithValue("@p", prefix);
            updated += await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var byTradition = new Dictionary<string, long>();
        var counts = conn.CreateCommand();
        counts.CommandText = "SELECT COALESCE(Tradition, '(untagged)'), COUNT(*) FROM Chunks GROUP BY 1";
        await using (var r = await counts.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                byTradition[r.GetString(0)] = r.GetInt64(1);

        var unmatched = new List<string>();
        var left = conn.CreateCommand();
        left.CommandText = """
            SELECT DISTINCT SourceType,
                   CASE WHEN SourceType = 6 AND instr(Source, ', Chapter ') > 0
                        THEN substr(Source, 1, instr(Source, ', Chapter ') - 1) ELSE Source END
            FROM Chunks WHERE Tradition IS NULL LIMIT 25
            """;
        await using (var r = await left.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                unmatched.Add($"{(SourceType)r.GetInt32(0)}: {r.GetString(1)}");

        if (updated > 0)
            _log.LogInformation("[Store] Backfilled ModuleId/Tradition on {Rows} row(s)", updated);
        foreach (var kv in byTradition.OrderByDescending(k => k.Value))
            _log.LogInformation("[Store]   {Tradition}: {Count} chunk(s)", kv.Key, kv.Value);
        if (unmatched.Count > 0)
            _log.LogWarning("[Store] Untagged sources (treated as Unclassified): {Sources}",
                string.Join("; ", unmatched));

        return new BackfillReport(updated, byTradition, unmatched);
    }

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
            "       SourceType, Language, ModuleId, Tradition " +
            "FROM Chunks";

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
                ModuleId = reader.IsDBNull(11) ? null : reader.GetString(11),
                Tradition = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }

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

    // ── Completion markers ─────────────────────────────────────────────────

    /// <summary>
    /// For an index built before completion markers existed: treat every module that already has
    /// tagged rows as complete. Call after <see cref="BackfillTraditionsAsync"/>.
    /// </summary>
    public async Task SeedCompletionMarkersAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO IndexedModules (ModuleId, SourceType, CompletedAt)
            SELECT DISTINCT ModuleId, SourceType, datetime('now') FROM Chunks
            WHERE ModuleId IS NOT NULL AND SourceType IN (2, 6)
            """;
        var n = await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("[Store] Marked {Count} existing module(s) as fully indexed", n);
    }

    public async Task<HashSet<string>> CompletedModuleIdsAsync(SourceType type, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ModuleId FROM IndexedModules WHERE SourceType = @st";
        cmd.Parameters.AddWithValue("@st", (int)type);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        return ids;
    }

    public async Task MarkModuleCompleteAsync(SourceType type, string moduleId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO IndexedModules (ModuleId, SourceType, CompletedAt)
            VALUES (@m, @st, datetime('now'))
            """;
        cmd.Parameters.AddWithValue("@m", moduleId);
        cmd.Parameters.AddWithValue("@st", (int)type);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Removes any rows (and the completion marker) for a module so it can be indexed cleanly.</summary>
    public async Task ResetModuleAsync(SourceType type, string moduleId, CancellationToken ct = default)
    {
        await using (var conn = OpenConnection())
        {
            await conn.OpenAsync(ct);
            var del = conn.CreateCommand();
            del.CommandText = """
                DELETE FROM Chunks WHERE SourceType = @st AND
                    (ModuleId = @m OR (@st = 2 AND Source = @m))
                """;
            del.Parameters.AddWithValue("@st", (int)type);
            del.Parameters.AddWithValue("@m", moduleId);
            var removed = await del.ExecuteNonQueryAsync(ct);

            var marker = conn.CreateCommand();
            marker.CommandText = "DELETE FROM IndexedModules WHERE ModuleId = @m AND SourceType = @st";
            marker.Parameters.AddWithValue("@m", moduleId);
            marker.Parameters.AddWithValue("@st", (int)type);
            await marker.ExecuteNonQueryAsync(ct);

            if (removed > 0)
                _log.LogInformation("[Store] Removed {Count} partial row(s) of {Module} before re-indexing", removed, moduleId);
        }

        _cacheLock.EnterWriteLock();
        try
        {
            _cache.RemoveAll(c => c.SourceType == type &&
                string.Equals(ModuleCatalog.ModuleKey(c), moduleId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _cacheLock.ExitWriteLock(); }
    }

    // ── Introspection ──────────────────────────────────────────────────────

    /// <summary>Module ids that already have chunks of the given type (used to index only what's missing).</summary>
    public HashSet<string> IndexedModuleIds(SourceType type)
    {
        _cacheLock.EnterReadLock();
        try
        {
            return _cache
                .Where(c => c.SourceType == type)
                .Select(ModuleCatalog.ModuleKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally { _cacheLock.ExitReadLock(); }
    }

    /// <summary>Distinct tradition tags present in the index (untagged rows count as Unclassified).</summary>
    public List<string> IndexedTraditions()
    {
        _cacheLock.EnterReadLock();
        try
        {
            return _cache
                .Select(c => c.Tradition ?? Traditions.Unclassified)
                .Distinct()
                .ToList();
        }
        finally { _cacheLock.ExitReadLock(); }
    }

    // ── Semantic search with MMR ───────────────────────────────────────────

    /// <summary>
    /// Cosine search over the filtered pool, then greedy MMR selection. The per-module cap is
    /// enforced <em>during</em> selection, so a capped module is skipped and the next-best chunk
    /// from another module takes its place (rather than the result just getting shorter).
    /// </summary>
    public List<ScoredChunk> Search(
        float[] queryEmbedding,
        RetrievalFilter filter,
        int topK = 8,
        float lambda = 0.6f,
        int candidateK = 80)
    {
        List<DocumentChunk> snapshot;
        _cacheLock.EnterReadLock();
        try
        {
            if (_cache.Count == 0) return [];
            snapshot = [.. _cache];
        }
        finally { _cacheLock.ExitReadLock(); }

        var candidates = snapshot
            .Where(filter.Matches)
            .Select(c => (chunk: c, score: CosineSimilarity(queryEmbedding, c.Embedding)))
            .Where(x => x.score >= filter.MinScore)
            .OrderByDescending(x => x.score)
            .Take(candidateK)
            .ToList();

        if (candidates.Count == 0) return [];

        _log.LogInformation("[Store] Top {Count} candidates:\n{Scores}",
            Math.Min(10, candidates.Count),
            string.Join("\n", candidates.Take(10).Select((x, i) =>
                $"  [{i + 1}] {x.chunk.Source[..Math.Min(50, x.chunk.Source.Length)]} " +
                $"= {x.score:F3}")));

        var perModule = new Dictionary<string, int>(
            filter.ExistingPerModule ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);

        var selected = new List<(DocumentChunk chunk, float score)>();
        var remaining = candidates.ToList();

        while (selected.Count < topK && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmr = float.MinValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                if (filter.MaxPerModule > 0 &&
                    perModule.GetValueOrDefault(ModuleCatalog.ModuleKey(remaining[i].chunk)) >= filter.MaxPerModule)
                    continue;

                var relevance = remaining[i].score;
                var maxSim = selected.Count == 0
                    ? 0f
                    : selected.Max(s =>
                        CosineSimilarity(remaining[i].chunk.Embedding, s.chunk.Embedding));

                var mmr = lambda * relevance - (1f - lambda) * maxSim;
                if (mmr > bestMmr) { bestMmr = mmr; bestIdx = i; }
            }

            if (bestIdx < 0) break; // everything left belongs to a capped module

            var pick = remaining[bestIdx];
            selected.Add(pick);
            var key = ModuleCatalog.ModuleKey(pick.chunk);
            perModule[key] = perModule.GetValueOrDefault(key) + 1;
            remaining.RemoveAt(bestIdx);
        }

        return selected.Select(x => new ScoredChunk(x.chunk, x.score)).ToList();
    }

    // ── Verse-pinned retrieval ─────────────────────────────────────────────

    /// <summary>Chunks whose verse range overlaps [verseStart, verseEnd] in the given chapter.</summary>
    public List<DocumentChunk> SearchByVerse(
        int bookNumber,
        int chapter,
        int verseStart,
        int verseEnd,
        RetrievalFilter? filter = null)
    {
        List<DocumentChunk> snapshot;
        _cacheLock.EnterReadLock();
        try { snapshot = [.. _cache]; }
        finally { _cacheLock.ExitReadLock(); }

        var q = snapshot.Where(c =>
            c.BookNumber == bookNumber &&
            c.ChapterBegin == chapter &&
            c.VerseBegin <= verseEnd &&
            c.VerseEnd >= verseStart);

        if (filter is not null) q = q.Where(filter.Matches);

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
        check.CommandText =
            $"SELECT COUNT(*) FROM pragma_table_info('Chunks') WHERE name='{column}'";
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

/// <summary>Result of <see cref="SqliteVectorStore.BackfillTraditionsAsync"/>.</summary>
public sealed record BackfillReport(
    long RowsUpdated,
    IReadOnlyDictionary<string, long> ChunksByTradition,
    IReadOnlyList<string> UnmatchedSources);
