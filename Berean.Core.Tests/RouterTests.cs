using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

public class RouterTests
{
    private static readonly object JohnChapter = new
    {
        book = 43, bookName = "John", chapter = 3, moduleId = "KJV",
        verses = new[]
        {
            new { book = 43, chapter = 3, verse = 16, bookName = "John", reference = "John 3:16", text = "For God so loved the world." },
            new { book = 43, chapter = 3, verse = 17, bookName = "John", reference = "John 3:17", text = "For God sent not his Son to condemn the world." },
        },
    };

    private static object Commentary(string module, string text) => new
    {
        moduleId = module, book = 43, bookName = "John", chapter = 3,
        entries = new[] { new { book = 43, bookName = "John", chapter = 3, verseBegin = 16, verseEnd = 16, reference = "John 3:16", marker = (string?)null, text } },
    };

    private static StubApiHandler Api() => new StubApiHandler()
        .On("api/bible/KJV/John/3", JohnChapter)
        .On("api/commentary/barnes/", Commentary("barnes", "Barnes on love and the sabbath."))
        .On("api/commentary/clarke/", Commentary("clarke", "Clarke on the world."))
        .On("api/commentary/henry/", Commentary("henry", "Henry on believing."))
        .On("api/commentary/sdabc/", Commentary("sdabc", "SDABC on John 3:16."));

    private static async Task<QueryRouter> RouterAsync(
        TempDb db, StubApiHandler api, IEnumerable<DocumentChunk>? chunks = null, Action<RetrievalOptions>? tweak = null)
    {
        var (pipeline, _) = await Fixtures.PipelineAsync(db.Path, chunks ??
        [
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath rest", index: 0),
            Fixtures.Chunk("clarke", Traditions.Wesleyan, "sabbath day", index: 0),
            Fixtures.Chunk("sdabc", Traditions.Adventist, "sabbath seventh day", index: 0),
            Fixtures.Chunk("egw", Traditions.Adventist, "sabbath sabbath sabbath", SourceType.Book,
                source: "The Desire of Ages, Chapter 12 — The Sabbath"),
        ]);
        var cfg = Fixtures.Config();
        tweak?.Invoke(cfg);
        return new QueryRouter(pipeline, cfg, api.Client(), "en", NullLogger.Instance);
    }

    [Fact]
    public async Task SdaOff_NothingAdventist_AndNoAdventistCommentaryIsEvenRequested()
    {
        using var db = new TempDb();
        var api = Api();
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("Explain John 3:16 and the sabbath", new RouteOptions(IncludeSda: false));

        Assert.All(r.Sources, s => Assert.NotEqual(Traditions.Adventist, s.Tradition));
        Assert.Null(r.AdventistContext);
        Assert.DoesNotContain("ADVENTIST SOURCES", r.Text);
        Assert.DoesNotContain(api.Requests, u => u.Contains("commentary/sdabc"));
        Assert.All(r.Sources, s => Assert.StartsWith("S", s.Id));
    }

    [Fact]
    public async Task Verse_FetchesExactBibleText_AndOneEntryPerModule()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, Api());

        var r = await router.RouteAsync("Explain John 3:16", new RouteOptions());

        Assert.Contains("VERSE TEXT:", r.Text);
        Assert.Contains("[KJV] John 3:16 — For God so loved the world.", r.Text);
        foreach (var module in new[] { "barnes", "clarke", "henry" })
            Assert.Single(r.Sources, s => s.ModuleId == module && s.Kind == "commentary" && s.Chunk().Locator is null && s.Chunk().Id.StartsWith("api:"));
        Assert.Contains("[S1] ", r.Text);
        Assert.Contains("(Evangelical, 19th c.) — John 3:16", r.Text);
    }

    [Fact]
    public async Task Verse_Range_ReturnsEveryVerseInIt()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, Api());

        var r = await router.RouteAsync("What does John 3:16-17 say?", new RouteOptions());

        Assert.Contains("16 For God so loved the world. 17 For God sent not his Son", r.Text);
    }

    [Fact]
    public async Task SdaOn_AddsASeparateAdventistBlock_WithAdventistIds()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, Api());

        var r = await router.RouteAsync("Explain John 3:16 and the sabbath", new RouteOptions(IncludeSda: true));

        Assert.NotNull(r.AdventistContext);
        Assert.StartsWith("ADVENTIST SOURCES:", r.AdventistContext);
        var advent = r.Sources.Where(s => s.Tradition == Traditions.Adventist).ToList();
        Assert.NotEmpty(advent);
        Assert.All(advent, s => Assert.StartsWith("A", s.Id));
        // Nothing Adventist leaks into the neutral block.
        Assert.DoesNotContain("SDABC", r.MainContext);
        Assert.DoesNotContain("The Desire of Ages", r.MainContext);
        Assert.Contains("[A1] ", r.AdventistContext);
    }

    [Fact]
    public async Task Conceptual_ExcludesAdventistBooks_AndCapsEachModule()
    {
        using var db = new TempDb();
        var many = Enumerable.Range(0, 6)
            .Select(i => Fixtures.Chunk("henry", Traditions.Reformed, $"sabbath sabbath note {i}", index: i))
            .Concat([
                Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath rest"),
                Fixtures.Chunk("egw", Traditions.Adventist, "sabbath", SourceType.Book, source: "The Desire of Ages, Chapter 1 — A"),
            ]);
        var router = await RouterAsync(db, Api(), many);

        var r = await router.RouteAsync("What does the Bible say about the sabbath?", new RouteOptions());

        Assert.DoesNotContain(r.Sources, s => s.Tradition == Traditions.Adventist);
        Assert.True(r.Sources.Count(s => s.ModuleId == "henry") <= 2);
        Assert.Contains(r.Sources, s => s.ModuleId == "barnes");
    }

    [Fact]
    public async Task Definition_LooksUpTheWord_InTheDictionaries()
    {
        using var db = new TempDb();
        var api = Api().On("api/dictionary/strong/lookup", new { topic = "H2617", definition = "chesed: steadfast love, mercy" });
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("What does H2617 mean?", new RouteOptions());

        Assert.Equal(QueryIntent.Definition, r.Intent);
        var entry = Assert.Single(r.Sources, s => s.Kind == "dictionary");
        Assert.Equal(Traditions.Lexical, entry.Tradition);
        Assert.Contains("steadfast love", r.Text);
        Assert.Contains("H2617", entry.Label);
    }

    [Fact]
    public async Task Compare_GivesEachInterpretiveTraditionAChunk_AndSkipsWeakOnes()
    {
        using var db = new TempDb();
        var chunks = new[]
        {
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath rest"),
            Fixtures.Chunk("clarke", Traditions.Wesleyan, "sabbath day"),
            Fixtures.Chunk("henry", Traditions.Reformed, "grace alone"),          // unrelated to the query
            Fixtures.Chunk("egw", Traditions.Adventist, "sabbath", SourceType.Book, source: "The Desire of Ages, Chapter 1 — A"),
        };
        var router = await RouterAsync(db, Api(), chunks);

        var r = await router.RouteAsync("What about the sabbath?", new RouteOptions(Mode: QueryMode.Compare));

        var traditions = r.Sources.Select(s => s.Tradition).ToHashSet();
        Assert.Contains(Traditions.Evangelical, traditions);
        Assert.Contains(Traditions.Wesleyan, traditions);
        Assert.DoesNotContain(Traditions.Reformed, traditions);   // below the similarity floor
        Assert.DoesNotContain(Traditions.Adventist, traditions);  // SDA toggle is off
    }

    [Fact]
    public async Task Compare_WithSdaOn_PutsAdventistInItsOwnBlock()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, Api());

        var r = await router.RouteAsync("What about the sabbath?", new RouteOptions(IncludeSda: true, Mode: QueryMode.Compare));

        Assert.NotNull(r.AdventistContext);
        Assert.DoesNotContain(Traditions.Adventist, r.MainContext ?? "");
    }

    [Fact]
    public async Task SelectedVerseTag_PinsRetrieval_WhenTheQuestionHasNoReference()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, Api());

        var r = await router.RouteAsync("[Translation: KJV]\n[Selected verse: John 3:16]\n\nWhy did he come?", new RouteOptions());

        Assert.Equal(QueryIntent.Verse, r.Intent);
        Assert.Contains("For God so loved the world.", r.Text);
    }

    [Fact]
    public async Task NothingRelevant_ReturnsNoContext()
    {
        using var db = new TempDb();
        var router = await RouterAsync(db, new StubApiHandler(), chunks: []);

        var r = await router.RouteAsync("hello there", new RouteOptions());

        Assert.Null(r.Text);
        Assert.Empty(r.Sources);
    }
}

internal static class SourceExtensions
{
    public static DocumentChunk Chunk(this ContextSource s) => s.Scored.Chunk;
}
