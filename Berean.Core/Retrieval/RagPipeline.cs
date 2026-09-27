using Microsoft.Extensions.Logging;

namespace Berean.Core.Retrieval;

/// <summary>
/// Orchestrates the RAG lifecycle: index → embed → store → retrieve.
///
/// Content comes from BereanResource.Api (see <see cref="ApiIndexer"/>). Every chunk is tagged
/// with its module and tradition so retrieval can filter by tradition and cap each module.
/// Bibles and dictionaries are not embedded: verse text and word definitions are fetched exactly.
/// </summary>
public class RagPipeline
{
    private readonly IEmbeddingService _embedder;
    private readonly SqliteVectorStore _store;
    private readonly ILogger _log;

    public int IndexedChunks => _store.Count;

    /// <summary>Modules in the library that are not (fully) in the index yet.</summary>
    public IReadOnlyList<string> PendingModules { get; internal set; } = [];

    /// <summary>The modules the resource API reported when the pipeline was created.</summary>
    public ModuleCatalog Catalog { get; internal set; } = ModuleCatalog.Empty;

    public RagPipeline(IEmbeddingService embedder, SqliteVectorStore store, ILogger log)
    {
        _embedder = embedder;
        _store = store;
        _log = log;
    }

    // ── Indexing ───────────────────────────────────────────────────────────

    /// <summary>Embeds and stores pre-built chunks. Used by <see cref="ApiIndexer"/>.</summary>
    public async Task IndexChunksAsync(
        List<DocumentChunk> chunks, string sourceName, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        _log.LogInformation("[RAG] Embedding {Count} chunks from {Source}", chunks.Count, sourceName);

        await _embedder.EmbedChunksAsync(chunks, ct, (done, total) =>
        {
            if (done % 50 == 0 || done == total)
                _log.LogInformation("[RAG] Embedded {Done}/{Total}", done, total);
        });

        await _store.AddAsync(chunks, ct);
        _log.LogInformation("[RAG] Stored {Count} chunks from {Source}", chunks.Count, sourceName);
    }

    public Task<bool> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    // ── Completion markers / introspection (delegates to the store) ────────

    public Task<HashSet<string>> CompletedModuleIdsAsync(SourceType type, CancellationToken ct = default)
        => _store.CompletedModuleIdsAsync(type, ct);

    public Task MarkModuleCompleteAsync(SourceType type, string moduleId, CancellationToken ct = default)
        => _store.MarkModuleCompleteAsync(type, moduleId, ct);

    public Task ResetModuleAsync(SourceType type, string moduleId, CancellationToken ct = default)
        => _store.ResetModuleAsync(type, moduleId, ct);

    /// <summary>Distinct tradition tags present in the index.</summary>
    public List<string> IndexedTraditions() => _store.IndexedTraditions();

    // ── Retrieval ──────────────────────────────────────────────────────────

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
        => _embedder.EmbedAsync(query, ct);

    /// <summary>
    /// Semantic search (cosine + MMR) restricted by <paramref name="filter"/>, including the
    /// tradition filter and the per-module cap. Returns chunks, not text: formatting and
    /// numbering the sources for the prompt is <see cref="ContextFormatter"/>'s job.
    /// </summary>
    public List<ScoredChunk> Search(
        float[] queryEmbedding,
        RetrievalFilter filter,
        int topK = 8,
        float lambda = 0.6f,
        int candidateK = 80)
    {
        if (_store.Count == 0)
        {
            _log.LogWarning("[RAG] Store is empty");
            return [];
        }

        var results = _store.Search(queryEmbedding, filter, topK, lambda, candidateK);

        if (results.Count > 0)
            _log.LogInformation("[RAG] {Count} chunks from {Modules} module(s) | types={Types} include={Inc} exclude={Exc}",
                results.Count,
                results.Select(r => ModuleCatalog.ModuleKey(r.Chunk)).Distinct().Count(),
                filter.SourceTypes is null ? "all" : string.Join(",", filter.SourceTypes),
                filter.IncludeTraditions is null ? "-" : string.Join(",", filter.IncludeTraditions),
                filter.ExcludeTraditions is null ? "-" : string.Join(",", filter.ExcludeTraditions));

        return results;
    }

    public async Task<List<ScoredChunk>> SearchAsync(
        string query,
        RetrievalFilter filter,
        int topK = 8,
        float lambda = 0.6f,
        int candidateK = 80,
        CancellationToken ct = default)
    {
        if (_store.Count == 0) return [];
        var embedding = await _embedder.EmbedAsync(query, ct);
        return Search(embedding, filter, topK, lambda, candidateK);
    }

    /// <summary>Verse-pinned lookup in the index: chunks whose verse range overlaps the given range.</summary>
    public List<DocumentChunk> SearchByVerse(
        int bookNumber, int chapter, int verseStart, int verseEnd, RetrievalFilter? filter = null)
        => _store.SearchByVerse(bookNumber, chapter, verseStart, verseEnd, filter);

    // ── Factory ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the pipeline immediately; the second element of the tuple is the background
    /// indexing task (already complete when the index is up to date).
    ///
    /// Startup order matters:
    ///   1. open/migrate the store (adds tradition columns to an older index)
    ///   2. load the module catalog from the resource API
    ///   3. backfill ModuleId/Tradition on existing rows (no re-embedding)
    ///   4. load the cache
    ///   5. work out which modules are missing or were interrupted, and index them if allowed
    /// </summary>
    public static async Task<(RagPipeline pipeline, Task indexingWork)> CreateFromApiAsync(
        RetrievalOptions config,
        BereanResourceApiClient client,
        ILoggerFactory logFactory,
        string embeddingModel,
        string ollamaEndpoint = "http://localhost:11434",
        CancellationToken ct = default)
    {
        var log = logFactory.CreateLogger<RagPipeline>();
        var embedder = new EmbeddingService(embeddingModel, ollamaEndpoint);
        var store = new SqliteVectorStore(
            config.RagDbPath,
            logFactory.CreateLogger<SqliteVectorStore>());

        await store.InitialiseAsync(ct);

        var catalog = await ModuleCatalog.LoadAsync(client, log, ct);
        var pipeline = new RagPipeline(embedder, store, log) { Catalog = catalog };

        if (catalog.Commentaries.Count > 0 || catalog.Books.Count > 0)
        {
            await store.BackfillTraditionsAsync(catalog, ct);
            if (store.CompletionMarkersCreated)
                await store.SeedCompletionMarkersAsync(ct);
        }

        if (await pipeline.LoadAsync(ct))
            log.LogInformation("[RAG] Loaded {Count} chunks from {Db}", pipeline.IndexedChunks, config.RagDbPath);

        if (catalog.Commentaries.Count + catalog.Books.Count == 0)
            return (pipeline, Task.CompletedTask);

        var plan = await ApiIndexer.PlanAsync(pipeline, catalog, config.AllowedCommentaryModules, ct);
        pipeline.PendingModules = plan.Commentaries.Concat(plan.Books).Select(m => m.ModuleId).ToList();

        if (plan.IsEmpty)
        {
            log.LogInformation("[RAG] Index is up to date ({Count} chunks)", pipeline.IndexedChunks);
            return (pipeline, Task.CompletedTask);
        }

        if (!config.AutoIndexMissingModules)
        {
            log.LogWarning(
                "[RAG] {Count} module(s) are not in the index: {Ids}. Verse questions still read them " +
                "directly from the API, but semantic search skips them. Set " +
                "Agents:BibleAgent:AutoIndexMissingModules=true to build them in the background " +
                "(slow: a full commentary can take hours to embed).",
                plan.ModuleCount, string.Join(", ", pipeline.PendingModules));
            return (pipeline, Task.CompletedTask);
        }

        log.LogInformation("[RAG] Indexing {Count} missing module(s) in the background: {Ids}",
            plan.ModuleCount, string.Join(", ", pipeline.PendingModules));

        var indexingWork = Task.Run(async () =>
        {
            await ApiIndexer.IndexAsync(pipeline, client, plan, config.Language,
                config.ChunkSize, config.ChunkOverlap, log, ct,
                config.CommentaryChunkSize, config.CommentaryChunkOverlap);
            pipeline.PendingModules = [];

            log.LogInformation("[RAG] Background indexing complete — {Count} chunks", pipeline.IndexedChunks);
        }, ct);

        return (pipeline, indexingWork);
    }
}
