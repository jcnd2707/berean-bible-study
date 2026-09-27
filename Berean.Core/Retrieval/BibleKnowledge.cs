using Microsoft.Extensions.Logging;

namespace Berean.Core.Retrieval;

/// <summary>
/// Everything about the library that doesn't depend on who is chatting: the vector index (loaded
/// into memory once), the resource API client and the dictionary list. Built once and shared by
/// every conversation, instead of each connection reloading the index.
/// </summary>
public sealed class BibleKnowledge
{
    public RetrievalOptions Config { get; }
    public RagPipeline Rag { get; }
    public BereanResourceApiClient Api { get; }
    public IReadOnlyList<string> DictionaryModuleIds { get; }

    /// <summary>Background indexing of missing modules (already complete when there is nothing to do).</summary>
    public Task IndexingTask { get; private set; }
    public bool IsIndexing => !IndexingTask.IsCompleted;

    /// <summary>Modules in the library that are not (fully) in the index yet.</summary>
    public IReadOnlyList<string> PendingModules => Rag.PendingModules;
    public int IndexedChunks => Rag.IndexedChunks;

    private BibleKnowledge(
        RetrievalOptions config, RagPipeline rag, BereanResourceApiClient api,
        IReadOnlyList<string> dictionaryModuleIds, Task indexingTask)
    {
        Config = config;
        Rag = rag;
        Api = api;
        DictionaryModuleIds = dictionaryModuleIds;
        IndexingTask = indexingTask;
    }

    /// <summary>
    /// Starts indexing whatever is missing or was interrupted, regardless of AutoIndexMissingModules.
    /// Existing chunks are never touched. Returns how many modules will be indexed.
    /// </summary>
    public async Task<int> StartIndexingAsync(ILogger log, CancellationToken lifetime = default)
    {
        if (IsIndexing) return Rag.PendingModules.Count;

        var plan = await ApiIndexer.PlanAsync(Rag, Rag.Catalog, Config.AllowedCommentaryModules, lifetime);
        Rag.PendingModules = plan.Commentaries.Concat(plan.Books).Select(m => m.ModuleId).ToList();
        if (plan.IsEmpty) return 0;

        log.LogInformation("[RAG] Indexing {Count} module(s): {Ids}", plan.ModuleCount, string.Join(", ", Rag.PendingModules));
        IndexingTask = Task.Run(async () =>
        {
            await ApiIndexer.IndexAsync(Rag, Api, plan, Config.Language, Config.ChunkSize, Config.ChunkOverlap,
                log, lifetime, Config.CommentaryChunkSize, Config.CommentaryChunkOverlap);
            Rag.PendingModules = [];
            log.LogInformation("[RAG] Indexing complete — {Count} chunks", Rag.IndexedChunks);
        }, lifetime);
        return plan.ModuleCount;
    }

    /// <param name="lifetime">Cancels background indexing (application shutdown), not a single chat connection.</param>
    public static async Task<BibleKnowledge> CreateAsync(
        RetrievalOptions config,
        ILoggerFactory logFactory,
        string embeddingModel,
        string ollamaEndpoint,
        CancellationToken lifetime = default)
    {
        if (string.IsNullOrWhiteSpace(config.ResourceApiBaseUrl))
            throw new InvalidOperationException(
                "Agents:BibleAgent:ResourceApiBaseUrl is required (the URL of BereanResource.Api, e.g. http://localhost:5121).");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.RagDbPath)) ?? "index");

        var client = new BereanResourceApiClient(config.ResourceApiBaseUrl);
        var (rag, indexing) = await RagPipeline.CreateFromApiAsync(
            config, client, logFactory, embeddingModel, ollamaEndpoint, lifetime);

        var dictionaries = (await client.GetDictionariesAsync(lifetime)).Select(m => m.ModuleId).ToList();

        return new BibleKnowledge(config, rag, client, dictionaries, indexing);
    }
}
