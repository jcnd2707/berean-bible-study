using HybridAgent.Core.RAG;
using Microsoft.Extensions.Logging;

namespace HybridAgent.RAG;

/// <summary>
/// Orchestrates the full RAG lifecycle for any agent:
///   index source files → embed → store in rag.db → retrieve at query time.
///
/// File routing by extension:
///   .bblx / .cmtx / .dctx / .topx / .devx  → ESwordReader  (Bible agent)
///   .txt / .md / .cs                         → plain-text reader
///   .pdf                                     → plain-text extraction (pdfpig or similar)
///
/// The chunker, embedder, cosine similarity, and context formatting are
/// shared across all agents — only the file reader changes per extension.
/// </summary>
public class RagPipeline
{
    private readonly EmbeddingService _embedder;
    private readonly SqliteVectorStore _store;
    private readonly ILogger _log;

    // Extensions handled by ESwordReader
    private static readonly HashSet<string> ESwordExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bblx", ".cmtx", ".dctx", ".topx", ".devx", ".refx", ".harx" };

    // Extensions read as plain text
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".cs", ".json", ".xml", ".html", ".htm" };

    public int IndexedChunks => _store.Count;

    public RagPipeline(EmbeddingService embedder, SqliteVectorStore store, ILogger log)
    {
        _embedder = embedder;
        _store = store;
        _log = log;
    }

    // ── Indexing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Index every file in the provided list.
    /// Each file is routed to the correct reader based on its extension.
    /// Unsupported extensions are skipped with a warning.
    /// </summary>
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
        try
        {
            records = await ESwordReader.ReadAsync(path, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RAG] Failed to read {File}", Path.GetFileName(path));
            return;
        }

        _log.LogInformation("[RAG] {Count} records from {File}",
            records.Count, Path.GetFileName(path));

        var chunks = new List<DocumentChunk>();
        foreach (var record in records)
        {
            chunks.AddRange(DocumentChunker.Chunk(
                record.Text, record.Source, chunkSize, overlap,
                record.BookNumber, record.ChapterBegin,
                record.VerseBegin, record.VerseEnd));
        }

        await EmbedAndStoreAsync(chunks, path, ct);
    }

    private async Task IndexPlainTextFileAsync(
        string path, int chunkSize, int overlap, CancellationToken ct)
    {
        _log.LogInformation("[RAG] Reading text file: {File}", Path.GetFileName(path));

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RAG] Failed to read {File}", Path.GetFileName(path));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        var chunks = DocumentChunker.Chunk(text, Path.GetFileName(path), chunkSize, overlap);
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

    public Task<bool> LoadAsync(CancellationToken ct = default) =>
        _store.LoadAsync(ct);

    // ── Retrieval ──────────────────────────────────────────────────────────

    /// <summary>Semantic search — unchanged from original.</summary>
    public async Task<string?> BuildContextAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (_store.Count == 0)
        {
            _log.LogWarning("[RAG] Store is empty — no context injected");
            return null;
        }

        var queryEmbedding = await _embedder.EmbedAsync(query, ct);
        var results = _store.Search(queryEmbedding, topK);

        if (results.Count == 0) return null;

        _log.LogDebug("[RAG] Retrieved {Count} chunks for query", results.Count);
        return FormatContext(results);
    }

    /// <summary>
    /// Verse-pinned retrieval — available for any agent that indexed e-Sword files.
    /// Returns null when no chunks cover the requested verse.
    /// </summary>
    public string? BuildVerseContext(int bookNumber, int chapter, int verse)
    {
        var results = _store.SearchByVerse(bookNumber, chapter, verse);
        if (results.Count == 0)
        {
            _log.LogDebug("[RAG] No chunks for {Book}:{Ch}:{V}", bookNumber, chapter, verse);
            return null;
        }
        _log.LogDebug("[RAG] Verse-pinned: {Count} chunks for {Book}:{Ch}:{V}",
            results.Count, bookNumber, chapter, verse);
        return FormatContext(results);
    }

    // ── Factory ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generic factory used by all agents.
    /// Loads rag.db if it already has data; otherwise scans ModulesRootPath
    /// and indexes every file whose extension is in AllowedExtensions.
    /// </summary>
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

        await store.InitialiseAsync(ct);

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
            log.LogWarning("[RAG] No files found in '{Root}' with extensions [{Exts}] — RAG disabled",
                config.ModulesRootPath,
                string.Join(", ", config.AllowedExtensions));
            return pipeline;
        }

        log.LogInformation("[RAG] Indexing {Count} file(s) from '{Root}'",
            files.Count, config.ModulesRootPath);

        await pipeline.IndexFilesAsync(files, ct: ct);

        log.LogInformation("[RAG] Indexing complete — {Count} total chunks", pipeline.IndexedChunks);

        return pipeline;
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    private static string FormatContext(List<DocumentChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Relevant reference material");
        sb.AppendLine();

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var label = c.BookNumber is not null
                ? $"{c.Source} — Book {c.BookNumber}, Ch {c.ChapterBegin}, v{c.VerseBegin}"
                : c.Source;
            sb.AppendLine($"### [{i + 1}] {label}");
            sb.AppendLine(c.Text);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Use the reference material above to inform your response. " +
                      "If it does not cover the question, say so clearly.");
        return sb.ToString();
    }
}