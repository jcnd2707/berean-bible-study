using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

/// <summary>Multiple references, chapter-only questions, duplicate verse records and word lookups.</summary>
public class RouterReferenceTests
{
    private static object Chapter(int book, string name, int chapter, params (int verse, string text)[] verses) => new
    {
        book, bookName = name, chapter, moduleId = "KJV",
        verses = verses.Select(v => new { book, chapter, v.verse, bookName = name, reference = $"{name} {chapter}:{v.verse}", v.text }).ToArray(),
    };

    private static object Commentary(string module, int book, string name, int chapter, params (int vb, int ve, string text)[] entries) => new
    {
        moduleId = module, book, bookName = name, chapter,
        entries = entries.Select(e => new { book, bookName = name, chapter, verseBegin = e.vb, verseEnd = e.ve, reference = $"{name} {chapter}", marker = (string?)null, e.text }).ToArray(),
    };

    private static async Task<QueryRouter> RouterAsync(TempDb db, StubApiHandler api)
    {
        var (pipeline, _) = await Fixtures.PipelineAsync(db.Path, []);
        return new QueryRouter(pipeline, Fixtures.Config(), api.Client(), "en", NullLogger.Instance);
    }

    [Fact]
    public void ParseVerses_FindsEveryReference_InOrder()
    {
        var refs = QueryRouter.ParseVerses("What does the Bible teach about the dead? Consider Ecclesiastes 9:5 and Luke 16:19-31.");

        Assert.Equal(2, refs.Count);
        Assert.Equal((21, 9, 5), (refs[0].BookNumber, refs[0].Chapter, refs[0].Verse));
        Assert.Equal((42, 16, 19, 31), (refs[1].BookNumber, refs[1].Chapter, refs[1].Verse ?? 0, refs[1].VerseEnd ?? 0));
    }

    [Fact]
    public void ParseVerses_IgnoresRepeatsAndCapsTheCount()
    {
        Assert.Single(QueryRouter.ParseVerses("John 3:16 ... and again John 3:16"));
        Assert.Equal(3, QueryRouter.ParseVerses("John 1:1 John 2:1 John 3:1 John 4:1").Count);
    }

    [Fact]
    public async Task EveryReferenceInTheQuestion_GetsItsVerseText_AndItsCommentary()
    {
        using var db = new TempDb();
        var api = new StubApiHandler()
            .On("api/bible/KJV/Ecclesiastes/9", Chapter(21, "Ecclesiastes", 9, (5, "the dead know not any thing")))
            .On("api/bible/KJV/Luke/16", Chapter(42, "Luke", 16, (19, "There was a certain rich man"), (20, "And a certain beggar named Lazarus")))
            .On("api/commentary/barnes/Ecclesiastes/9", Commentary("barnes", 21, "Ecclesiastes", 9, (5, 5, "Barnes on the dead")))
            .On("api/commentary/barnes/Luke/16", Commentary("barnes", 42, "Luke", 16, (19, 31, "Barnes on Lazarus")));
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("State of the dead? Consider Ecclesiastes 9:5 and Luke 16:19-31.", RouteOptions.None());

        Assert.Contains("Ecclesiastes 9:5 — the dead know not any thing", r.Text);
        Assert.Contains("Luke 16:19-31", r.Text);
        Assert.Contains("There was a certain rich man", r.Text);
        Assert.Equal(2, r.Sources.Count(s => s.ModuleId == "barnes"));
    }

    [Fact]
    public async Task DuplicateVerseRecords_AreCollapsed_AndVerseNumbersStripped()
    {
        using var db = new TempDb();
        var api = new StubApiHandler().On("api/bible/KJV/John/3", Chapter(43, "John", 3,
            (16, "16 For God so loved the world."), (16, "16 For God so loved the world."), (16, "16 For God so loved the world.")));
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("Explain John 3:16", RouteOptions.None());

        Assert.Contains("[KJV] John 3:16 — For God so loved the world.", r.Text);
        Assert.DoesNotContain("world. For God", r.Text);
    }

    [Fact]
    public async Task ChapterOnlyQuestion_GetsTheCommentaryEntriesThatMatchTheKeyWords()
    {
        using var db = new TempDb();
        var api = new StubApiHandler()
            .On("api/bible/KJV/Revelation/13", Chapter(66, "Revelation", 13, (1, "And I stood upon the sand of the sea")))
            .On("api/commentary/barnes/Revelation/13", Commentary("barnes", 66, "Revelation", 13,
                (1, 1, "The sea is a symbol of nations."),
                (2, 4, "The beast is a symbol of persecuting power, the beast receives the dragon's authority."),
                (11, 18, "A second beast, and the number of the beast.")))
            .On("api/commentary/clarke/Revelation/13", Commentary("clarke", 66, "Revelation", 13,
                (1, 1, "Nothing about the topic here.")));
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("Who or what is the beast of Revelation 13?", RouteOptions.None());

        var barnes = Assert.Single(r.Sources, s => s.ModuleId == "barnes");
        Assert.Contains("persecuting power", barnes.Scored.Chunk.Text);
        Assert.DoesNotContain("symbol of nations", barnes.Scored.Chunk.Text);
        Assert.DoesNotContain(r.Sources, s => s.ModuleId == "clarke"); // nothing relevant, so nothing added
    }

    [Fact]
    public void QueryTerms_DropReferencesFillerAndBookNames()
    {
        var terms = QueryRouter.QueryTerms("What does the Bible teach about the beast of Revelation 13?");

        Assert.Contains("beast", terms);
        Assert.DoesNotContain("revelation", terms);
        Assert.DoesNotContain("teach", terms);
    }

    [Fact]
    public async Task TransliteratedWord_IsFoundThroughTheTransliterationIndex()
    {
        using var db = new TempDb();
        var api = new StubApiHandler().On("api/dictionary/strong/transliteration",
            new[] { new { topic = "H5315", definition = "Original: נפשׁ Transliteration: nephesh soul, life, person" } });
        var router = await RouterAsync(db, api);

        var r = await router.RouteAsync("What does nephesh mean in the Old Testament?", RouteOptions.None());

        var entry = Assert.Single(r.Sources, s => s.Kind == "dictionary");
        Assert.Contains("H5315", entry.Label);
        Assert.Contains("soul, life, person", r.Text);
    }
}
