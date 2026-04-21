//using HybridAgent.Core.RAG;
//using System.Text.Json;

//namespace HybridAgent.Core.RAG;

///// <summary>
///// In-memory vector store with cosine-similarity search and JSON persistence.
/////
///// For production / large corpora consider replacing this with:
/////   - Qdrant   (docker run -p 6333:6333 qdrant/qdrant)
/////   - ChromaDB (pip install chromadb)
/////   - SQLite-VSS (zero-server, single file)
/////
///// For the three agents described (car / Bible / C#) this in-memory store
///// handles tens of thousands of chunks without issue.
///// </summary>
//public class VectorStore
//{
//    private readonly List<DocumentChunk> _chunks = [];
//    private readonly string? _persistPath;

//    public int Count => _chunks.Count;

//    public VectorStore(string? persistPath = null)
//    {
//        _persistPath = persistPath;
//    }

//    // ── Indexing ───────────────────────────────────────────────────────────

//    public void Add(IEnumerable<DocumentChunk> chunks) =>
//        _chunks.AddRange(chunks);

//    public void Clear() => _chunks.Clear();

//    // ── Search ─────────────────────────────────────────────────────────────

//    /// <summary>
//    /// Returns the top-k chunks most similar to the query embedding.
//    /// </summary>
//    public List<DocumentChunk> Search(float[] queryEmbedding, int topK = 5)
//    {
//        if (_chunks.Count == 0)
//            return [];

//        return _chunks
//            .Select(c => (chunk: c, score: CosineSimilarity(queryEmbedding, c.Embedding)))
//            .OrderByDescending(x => x.score)
//            .Take(topK)
//            .Select(x => x.chunk)
//            .ToList();
//    }

//    // ── Persistence ────────────────────────────────────────────────────────

//    /// <summary>
//    /// Save the index to a JSON file so you don't have to re-embed on every startup.
//    /// Embeddings are large — expect ~1 MB per 500 chunks with mxbai-embed-large.
//    /// </summary>
//    public async Task SaveAsync(string? path = null)
//    {
//        var target = path ?? _persistPath
//            ?? throw new InvalidOperationException("No persist path configured.");

//        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
//        var json = JsonSerializer.Serialize(_chunks, new JsonSerializerOptions { WriteIndented = false });
//        await File.WriteAllTextAsync(target, json);
//    }

//    /// <summary>
//    /// Load a previously saved index. Returns true if the file existed.
//    /// </summary>
//    public async Task<bool> LoadAsync(string? path = null)
//    {
//        var target = path ?? _persistPath;
//        if (target is null || !File.Exists(target)) return false;

//        var json = await File.ReadAllTextAsync(target);
//        var loaded = JsonSerializer.Deserialize<List<DocumentChunk>>(json);

//        if (loaded is null) return false;

//        _chunks.Clear();
//        _chunks.AddRange(loaded);
//        return true;
//    }

//    // ── Math ───────────────────────────────────────────────────────────────

//    private static float CosineSimilarity(float[] a, float[] b)
//    {
//        if (a.Length != b.Length) return 0f;

//        float dot = 0, normA = 0, normB = 0;
//        for (int i = 0; i < a.Length; i++)
//        {
//            dot += a[i] * b[i];
//            normA += a[i] * a[i];
//            normB += b[i] * b[i];
//        }

//        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
//        return denom == 0 ? 0f : dot / denom;
//    }
//}