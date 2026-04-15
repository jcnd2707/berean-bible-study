using Microsoft.Extensions.Logging;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Orchestrates the RAG lifecycle: index → embed → store → retrieve.
///
/// File type → SourceType mapping (used to tag every chunk):
///   .bblx          → SourceType.Bible
///   .cmtx          → SourceType.Commentary
///   .dctx / .lexx  → SourceType.Dictionary  (indexed but NOT used in RAG retrieval —
///                                             dictionaries are accessed via the lookup_word tool)
///   .topx / .devx  → SourceType.Topic
///   .txt / .md etc → SourceType.PlainText
///
/// Language is inferred from filename — filenames containing common Spanish indicators
/// ("RVR", "NVI", "DHH", "Reina", "Valera", "es_", "_es") are tagged "es", else "en".
/// </summary>
public class RagPipeline
{
    private readonly EmbeddingService _embedder;
    private readonly SqliteVectorStore _store;
    private readonly ILogger _log;

    private static readonly HashSet<string> ESwordExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bblx", ".cmtx", ".dctx", ".lexx", ".topx", ".devx", ".refx", ".harx" };

    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".cs", ".json", ".xml", ".html", ".htm" };

    // Spanish filename indicators — case-insensitive substring match
    private static readonly string[] SpanishIndicators =
        ["rvr", "nvi", "dhh", "reina", "valera", "es_", "_es", "lbla", "nbla", "tla"];

    public int IndexedChunks => _store.Count;

    public RagPipeline(EmbeddingService embedder, SqliteVectorStore store, ILogger log)
    {
        _embedder = embedder;
        _store = store;
        _log = log;
    }

    // ── Indexing ───────────────────────────────────────────────────────────

    public async Task IndexFilesAsync(
        IEnumerable<string> filePaths,
        int chunkSize = 500,
        int overlap = 100,
        CancellationToken ct = default)
    {
        foreach (var path in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                _log.LogWarning("[RAG] File not found, skipping: {Path}", path);
                continue;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ESwordExtensions.Contains(ext))
                await IndexESwordFileAsync(path, chunkSize, overlap, ct);
            else if (PlainTextExtensions.Contains(ext))
                await IndexPlainTextFileAsync(path, chunkSize, overlap, ct);
            else
                _log.LogWarning("[RAG] Unsupported extension '{Ext}', skipping: {File}",
                    ext, Path.GetFileName(path));
        }
    }

    // ── File readers ───────────────────────────────────────────────────────

    private async Task IndexESwordFileAsync(
        string path, int chunkSize, int overlap, CancellationToken ct)
    {
        _log.LogInformation("[RAG] Reading e-Sword module: {File}", Path.GetFileName(path));

        List<ESwordRecord> records;
        try { records = await ESwordReader.ReadAsync(path, ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RAG] Failed to read {File}", Path.GetFileName(path));
            return;
        }

        if (records.Count == 0)
        {
            _log.LogWarning("[RAG] No readable records in {File} (may be encrypted)", Path.GetFileName(path));
            return;
        }

        var sourceType = ClassifyESwordExtension(Path.GetExtension(path));
        var language = DetectLanguage(Path.GetFileName(path));

        _log.LogInformation("[RAG] {Count} records | type={Type} lang={Lang} | {File}",
            records.Count, sourceType, language, Path.GetFileName(path));

        var chunks = new List<DocumentChunk>();
        foreach (var record in records)
        {
            // Dictionary chunks are tagged but skipped from embedding —
            // they are accessed directly via the lookup_word tool instead.
            if (sourceType == SourceType.Dictionary)
                continue;

            chunks.AddRange(DocumentChunker.Chunk(
                record.Text, record.Source, chunkSize, overlap,
                record.BookNumber, record.ChapterBegin,
                record.VerseBegin, record.VerseEnd,
                sourceType, language));
        }

        if (chunks.Count > 0)
            await EmbedAndStoreAsync(chunks, path, ct);
        else
            _log.LogInformation("[RAG] Skipped embedding for {File} (dictionary — use lookup_word tool)",
                Path.GetFileName(path));
    }

    private async Task IndexPlainTextFileAsync(
        string path, int chunkSize, int overlap, CancellationToken ct)
    {
        _log.LogInformation("[RAG] Reading text file: {File}", Path.GetFileName(path));

        string text;
        try { text = await File.ReadAllTextAsync(path, ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RAG] Failed to read {File}", Path.GetFileName(path));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        var language = DetectLanguage(Path.GetFileName(path));
        var chunks = DocumentChunker.Chunk(text, Path.GetFileName(path),
            chunkSize, overlap,
            sourceType: SourceType.PlainText, language: language);

        await EmbedAndStoreAsync(chunks, path, ct);
    }

    private async Task EmbedAndStoreAsync(
        List<DocumentChunk> chunks, string sourcePath, CancellationToken ct)
    {
        if (chunks.Count == 0) return;

        _log.LogInformation("[RAG] Embedding {Count} chunks from {File}",
            chunks.Count, Path.GetFileName(sourcePath));

        await _embedder.EmbedChunksAsync(chunks, ct, (done, total) =>
        {
            if (done % 50 == 0 || done == total)
                _log.LogDebug("[RAG] Embedded {Done}/{Total}", done, total);
        });

        await _store.AddAsync(chunks, ct);
        _log.LogInformation("[RAG] Stored {Count} chunks from {File}",
            chunks.Count, Path.GetFileName(sourcePath));
    }

    // ── Persistence ────────────────────────────────────────────────────────

    public Task SaveAsync(string? _ = null) => Task.CompletedTask;
    public Task<bool> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    // ── Retrieval ──────────────────────────────────────────────────────────

    /// <summary>
    /// Semantic search with MMR, optional source type and language filters.
    /// </summary>
    public async Task<string?> BuildContextAsync(
        string query,
        int topK = 8,
        float lambda = 0.6f,
        int candidateK = 80,
        SourceType? sourceType = null,
        string? language = null,
        CancellationToken ct = default)
    {
        if (_store.Count == 0)
        {
            _log.LogWarning("[RAG] Store is empty");
            return null;
        }

        var queryEmbedding = await _embedder.EmbedAsync(query, ct);
        var results = _store.Search(queryEmbedding, topK, lambda, candidateK,
                                           sourceType, language);

        if (results.Count == 0) return null;

        var sources = results.Select(r => r.Source).Distinct().Count();
        _log.LogDebug("[RAG] {Count} chunks from {Sources} source(s) | type={Type} lang={Lang}",
            results.Count, sources, sourceType?.ToString() ?? "all", language ?? "all");

        return FormatContext(results);
    }

    /// <summary>
    /// Fan-out semantic search across multiple source types.
    /// Retrieves topK/types chunks per type, then re-ranks the combined set with MMR.
    /// </summary>
    public async Task<string?> BuildMultiSourceContextAsync(
        string query,
        IList<SourceType> sourceTypes,
        int topKPerType = 4,
        float lambda = 0.6f,
        string? language = null,
        CancellationToken ct = default)
    {
        if (_store.Count == 0) return null;

        var queryEmbedding = await _embedder.EmbedAsync(query, ct);
        var allResults = new List<DocumentChunk>();

        foreach (var st in sourceTypes)
        {
            var results = _store.Search(queryEmbedding, topKPerType, lambda,
                topKPerType * 10, st, language);
            allResults.AddRange(results);
        }

        if (allResults.Count == 0) return null;

        // Final MMR pass across the combined multi-source set to remove redundancy
        var finalResults = ApplyMmr(queryEmbedding, allResults, topKPerType * sourceTypes.Count, lambda);

        var sources = finalResults.Select(r => r.Source).Distinct().Count();
        _log.LogDebug("[RAG] Multi-source: {Count} chunks from {Sources} source(s)",
            finalResults.Count, sources);

        return FormatContext(finalResults);
    }

    /// <summary>
    /// Verse-pinned retrieval: returns all chunks covering book/chapter/verse.
    /// </summary>
    public string? BuildVerseContext(
        int bookNumber,
        int chapter,
        int verse,
        SourceType? sourceType = null,
        string? language = null)
    {
        var results = _store.SearchByVerse(bookNumber, chapter, verse, sourceType, language);
        if (results.Count == 0) return null;
        _log.LogDebug("[RAG] Verse-pinned: {Count} chunks for {Book}:{Ch}:{V}",
            results.Count, bookNumber, chapter, verse);
        return FormatContext(results);
    }

    // ── Factory ────────────────────────────────────────────────────────────

    public static async Task<RagPipeline> CreateAsync(
        AgentRagConfig config,
        ILoggerFactory logFactory,
        string embeddingModel = "nomic-embed-text",
        string ollamaEndpoint = "http://localhost:11434",
        CancellationToken ct = default)
    {
        var log = logFactory.CreateLogger<RagPipeline>();
        var embedder = new EmbeddingService(embeddingModel, ollamaEndpoint);
        var store = new SqliteVectorStore(config.RagDbPath);

        await store.InitialiseAsync(ct);  // also runs schema migration

        var pipeline = new RagPipeline(embedder, store, log);

        if (await pipeline.LoadAsync(ct))
        {
            log.LogInformation("[RAG] Loaded {Count} chunks from {Db}",
                pipeline.IndexedChunks, config.RagDbPath);
            return pipeline;
        }

        var files = config.ResolveFiles().ToList();

        if (files.Count == 0)
        {
            log.LogWarning("[RAG] No files found in '{Root}' matching [{Exts}]",
                config.ModulesRootPath,
                string.Join(", ", config.AllowedExtensions));
            return pipeline;
        }

        log.LogInformation("[RAG] Indexing {Count} file(s) from '{Root}'",
            files.Count, config.ModulesRootPath);

        await pipeline.IndexFilesAsync(files, ct: ct);
        log.LogInformation("[RAG] Complete — {Count} chunks indexed", pipeline.IndexedChunks);

        return pipeline;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static SourceType ClassifyESwordExtension(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".bblx" => SourceType.Bible,
            ".cmtx" => SourceType.Commentary,
            ".dctx" or ".lexx" => SourceType.Dictionary,
            ".topx" or ".devx"
                or ".refx" => SourceType.Topic,
            _ => SourceType.Unknown,
        };

    private static string DetectLanguage(string filename)
    {
        var lower = filename.ToLowerInvariant();
        return SpanishIndicators.Any(ind => lower.Contains(ind)) ? "es" : "en";
    }

    private static List<DocumentChunk> ApplyMmr(
        float[] queryEmbedding, List<DocumentChunk> candidates, int topK, float lambda)
    {
        var selected = new List<DocumentChunk>();
        var remaining = candidates.ToList();

        while (selected.Count < topK && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmr = float.MinValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                float rel = CosineSimilarity(queryEmbedding, remaining[i].Embedding);
                float maxSim = selected.Count == 0
                    ? 0f
                    : selected.Max(s => CosineSimilarity(remaining[i].Embedding, s.Embedding));

                float mmr = lambda * rel - (1f - lambda) * maxSim;
                if (mmr > bestMmr) { bestMmr = mmr; bestIdx = i; }
            }

            if (bestIdx < 0) break;
            selected.Add(remaining[bestIdx]);
            remaining.RemoveAt(bestIdx);
        }

        return selected;
    }

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

    private static string FormatContext(List<DocumentChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Relevant reference material");
        sb.AppendLine();

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var label = c.BookNumber is not null
                ? $"{c.Source} [{c.SourceType}] — Book {c.BookNumber}, Ch {c.ChapterBegin}, v{c.VerseBegin}"
                : $"{c.Source} [{c.SourceType}]";
            sb.AppendLine($"### [{i + 1}] {label}");
            sb.AppendLine(c.Text);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Use the material above to inform your answer. " +
                      "If it does not cover the question, say so clearly.");
        return sb.ToString();
    }
}