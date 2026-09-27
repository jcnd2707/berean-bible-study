using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Berean.Core.Tests;

/// <summary>Embeds by keyword so tests control similarity: dimension 0 = sabbath, 1 = death, 2 = grace.</summary>
internal sealed class FakeEmbedder : IEmbeddingService
{
    private static readonly string[] Keywords = ["sabbath", "death", "grace"];

    public static float[] Vector(string text)
    {
        var v = new float[Keywords.Length + 1];
        for (int i = 0; i < Keywords.Length; i++)
            if (text.Contains(Keywords[i], StringComparison.OrdinalIgnoreCase)) v[i] = 1f;
        v[^1] = 0.05f; // keeps every vector non-zero
        return v;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(Vector(text));

    public Task EmbedChunksAsync(List<DocumentChunk> chunks, CancellationToken ct = default, Action<int, int>? onProgress = null)
    {
        foreach (var c in chunks) c.Embedding = Vector(c.Text);
        return Task.CompletedTask;
    }
}

/// <summary>A throwaway index file, deleted on dispose.</summary>
internal sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"berean-test-{Guid.NewGuid():N}", "index.rag.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); } catch { /* best effort */ }
    }
}

internal static class Fixtures
{
    public static DocumentChunk Chunk(
        string module, string tradition, string text,
        SourceType type = SourceType.Commentary, int index = 0, string? source = null,
        int? book = null, int? chapter = null, int? verseBegin = null, int? verseEnd = null) => new()
    {
        BookNumber = book,
        ChapterBegin = chapter,
        VerseBegin = verseBegin,
        VerseEnd = verseEnd,
        Id = $"{module}::{index}::{text.GetHashCode()}",
        Source = source ?? module,
        ChunkIndex = index,
        Text = text,
        SourceType = type,
        Language = "en",
        ModuleId = module,
        Tradition = tradition,
        Embedding = FakeEmbedder.Vector(text),
    };

    public static ModuleCatalog Catalog() => new(
        commentaries:
        [
            new(ModuleKind.Commentary, "barnes", "Barnes", "Barnes' Notes on the Bible", Traditions.Evangelical, "19th c."),
            new(ModuleKind.Commentary, "clarke", "Clarke", "Adam Clarke's Commentary", Traditions.Wesleyan, "19th c."),
            new(ModuleKind.Commentary, "henry", "Henry", "Matthew Henry's Commentary", Traditions.Reformed, "18th c."),
            new(ModuleKind.Commentary, "sdabc", "SDABC", "SDA Bible Commentary", Traditions.Adventist, "20th c."),
        ],
        books:
        [
            new(ModuleKind.Book, "The_Desire_of_Ages", "The Desire of Ages", "The Desire of Ages", Traditions.Adventist, "19th c."),
        ],
        dictionaries:
        [
            new(ModuleKind.Dictionary, "strong", "Strong", "Strong's Dictionary", Traditions.Lexical, "19th c."),
        ],
        bookTitles: [("The Desire of Ages", "The_Desire_of_Ages")]);

    public static async Task<(RagPipeline pipeline, SqliteVectorStore store)> PipelineAsync(
        string dbPath, IEnumerable<DocumentChunk> chunks)
    {
        var store = new SqliteVectorStore(dbPath, NullLogger<SqliteVectorStore>.Instance);
        await store.InitialiseAsync();
        await store.AddAsync(chunks);
        var pipeline = new RagPipeline(new FakeEmbedder(), store, NullLogger.Instance) { Catalog = Catalog() };
        return (pipeline, store);
    }

    public static RetrievalOptions Config() => new()
    {
        Language = "en",
        TopK = 8,
        MmrLambda = 0.9f,
        MmrCandidateK = 100,
        MaxPerModule = 2,
        AdventistTopK = 4,
        AllowedBibleModules = ["KJV"],
        CompareMinScore = 0.5f,
    };
}

/// <summary>Serves canned JSON for BereanResource.Api paths; anything else is a 404.</summary>
internal sealed class StubApiHandler : HttpMessageHandler
{
    private readonly Dictionary<string, object> _routes = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requests { get; } = [];

    public StubApiHandler On(string pathContains, object body)
    {
        _routes[pathContains] = body;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        lock (Requests) Requests.Add(url);

        foreach (var (key, body) in _routes)
            if (url.Contains(key, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public BereanResourceApiClient Client() =>
        new(new HttpClient(this) { BaseAddress = new Uri("http://stub/") });
}
