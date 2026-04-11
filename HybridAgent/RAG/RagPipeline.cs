using Microsoft.Extensions.Logging;
using OpenAI.VectorStores;

namespace HybridAgent.RAG;

/// <summary>
/// Ties the chunker, embedding service, and vector store into one pipeline.
///
/// Typical lifecycle:
///   1. First run  → IndexDirectoryAsync() + SaveAsync()  (slow — embeds everything)
///   2. Later runs → LoadAsync()                          (fast — reads from disk)
///   3. At query   → BuildContextAsync()                  (returns injected text)
/// </summary>
public class RagPipeline
{
    private readonly EmbeddingService _embedder;
    private readonly VectorStore _store;
    private readonly ILogger _log;

    public int IndexedChunks => _store.Count;

    public RagPipeline(EmbeddingService embedder, VectorStore store, ILogger log)
    {
        _embedder = embedder;
        _store = store;
        _log = log;
    }

    // ── Indexing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Chunk + embed every .txt file in a directory and add them to the store.
    /// Call SaveAsync() afterwards to persist so you don't re-embed next time.
    /// </summary>
    public async Task IndexDirectoryAsync(
        string directory,
        string searchPattern = "*.txt",
        int chunkSize = 500,
        int overlap = 100,
        CancellationToken ct = default)
    {
        _log.LogInformation("[RAG] Indexing directory: {Dir}", directory);

        var chunks = await DocumentChunker.ChunkDirectoryAsync(
            directory, searchPattern, chunkSize, overlap);

        _log.LogInformation("[RAG] {Count} chunks created — embedding (this may take a minute)...", chunks.Count);

        await _embedder.EmbedChunksAsync(chunks, ct, (done, total) =>
        {
            if (done % 20 == 0 || done == total)
                _log.LogDebug("[RAG] Embedded {Done}/{Total}", done, total);
        });

        _store.Add(chunks);
        _log.LogInformation("[RAG] Indexed {Count} chunks total", _store.Count);
    }

    /// <summary>
    /// Chunk + embed a single string (e.g. content you built at runtime).
    /// </summary>
    public async Task IndexTextAsync(
        string text,
        string sourceName,
        int chunkSize = 500,
        int overlap = 100,
        CancellationToken ct = default)
    {
        var chunks = DocumentChunker.Chunk(text, sourceName, chunkSize, overlap);
        await _embedder.EmbedChunksAsync(chunks, ct);
        _store.Add(chunks);
        _log.LogInformation("[RAG] Indexed {Count} chunks from '{Source}'", chunks.Count, sourceName);
    }

    // ── Persistence ────────────────────────────────────────────────────────

    public Task SaveAsync(string path) => _store.SaveAsync(path);

    /// <summary>Returns true if an existing index was loaded from disk.</summary>
    public Task<bool> LoadAsync(string path) => _store.LoadAsync(path);

    // ── Retrieval ──────────────────────────────────────────────────────────

    /// <summary>
    /// Embed the query, search the store, and return a formatted context block
    /// ready to inject into a system or user prompt.
    /// Returns null if the store is empty.
    /// </summary>
    public async Task<string?> BuildContextAsync(
        string query,
        int topK = 5,
        float minSimilarity = 0.3f,
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

        // Build a clean context block the model can reason over
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Relevant reference material");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var chunk = results[i];
            sb.AppendLine($"### [{i + 1}] Source: {chunk.Source}");
            sb.AppendLine(chunk.Text);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Use the reference material above to inform your response. " +
                      "If the material doesn't cover the question, say so clearly.");

        return sb.ToString();
    }

    // ── Factory ────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a pipeline for one agent domain.
    /// Loads an existing index if found; otherwise indexes the documents directory.
    /// </summary>
    public static async Task<RagPipeline> CreateAsync(
        string docsDirectory,
        string indexPath,
        ILoggerFactory logFactory,
        string embeddingModel = "nomic-embed-text",
        string ollamaEndpoint = "http://localhost:11434",
        CancellationToken ct = default)
    {
        var log = logFactory.CreateLogger<RagPipeline>();
        var embedder = new EmbeddingService(embeddingModel, ollamaEndpoint);
        var store = new VectorStore(indexPath);
        var pipeline = new RagPipeline(embedder, store, log);

        if (await pipeline.LoadAsync(indexPath))
        {
            log.LogInformation("[RAG] Loaded existing index from {Path} ({Count} chunks)",
                indexPath, pipeline.IndexedChunks);
        }
        else if (Directory.Exists(docsDirectory))
        {
            log.LogInformation("[RAG] No existing index found — indexing {Dir}", docsDirectory);
            await pipeline.IndexDirectoryAsync(docsDirectory, ct: ct);
            await pipeline.SaveAsync(indexPath);
        }
        else
        {
            log.LogWarning("[RAG] No docs directory found at {Dir} — RAG disabled for this agent", docsDirectory);
        }

        return pipeline;
    }
}