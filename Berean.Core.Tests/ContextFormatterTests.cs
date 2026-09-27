using Xunit;

namespace Berean.Core.Tests;

public class ContextFormatterTests
{
    private static ScoredChunk Verse(string module, string tradition, string text)
    {
        var c = Fixtures.Chunk(module, tradition, text, book: 45, chapter: 8, verseBegin: 28, verseEnd: 28);
        return new ScoredChunk(c, 0.7f);
    }

    [Fact]
    public void Labels_CarryDisplayName_Tradition_Era_AndLocation()
    {
        var ctx = ContextFormatter.Format([], [Verse("barnes", Traditions.Evangelical, "All things work together.")], [], Fixtures.Catalog())!;

        Assert.Contains("[S1] Barnes' Notes on the Bible (Evangelical, 19th c.) — Romans 8:28:", ctx.Text);
        var s = Assert.Single(ctx.Sources);
        Assert.Equal("S1", s.Id);
        Assert.Equal("commentary", s.Kind);
        Assert.Equal(45, s.BookNumber);
        Assert.Equal(8, s.Chapter);
    }

    [Fact]
    public void BookSources_KeepTheirChapterLabel_AndExposeTheChapterIndex()
    {
        var book = new ScoredChunk(Fixtures.Chunk("The_Desire_of_Ages", Traditions.Adventist, "text", SourceType.Book,
            source: "The Desire of Ages, Chapter 12 — The Sabbath"), 0.6f);

        var ctx = ContextFormatter.Format([], [], [book], Fixtures.Catalog())!;

        Assert.Contains("[A1] The Desire of Ages, Chapter 12 — The Sabbath (Adventist, 19th c.):", ctx.Text);
        Assert.Equal(12, ctx.Sources.Single().BookChapterIndex);
        Assert.Equal("book", ctx.Sources.Single().Kind);
    }

    [Fact]
    public void AdventistSources_GetTheirOwnBlock_AfterTheMainMaterial()
    {
        var main = Verse("barnes", Traditions.Evangelical, "main text");
        var adv = Verse("sdabc", Traditions.Adventist, "adventist text");

        var ctx = ContextFormatter.Format([], [main], [adv], Fixtures.Catalog())!;

        Assert.True(ctx.Text.IndexOf("REFERENCE MATERIAL:") < ctx.Text.IndexOf("ADVENTIST SOURCES:"));
        Assert.DoesNotContain("adventist text", ctx.Main);
        Assert.Contains("adventist text", ctx.Adventist);
        Assert.Equal(["S1", "A1"], ctx.Sources.Select(s => s.Id));
    }

    [Fact]
    public void NoAdventistBlock_WhenThereAreNoAdventistSources()
    {
        var ctx = ContextFormatter.Format([], [Verse("barnes", Traditions.Evangelical, "x")], [], Fixtures.Catalog())!;

        Assert.Null(ctx.Adventist);
        Assert.DoesNotContain("ADVENTIST", ctx.Text);
    }

    [Fact]
    public void VerseText_ComesFirst_AndIsNotACitableSource()
    {
        var ctx = ContextFormatter.Format(
            [new BibleText("KJV", "Romans 8:28", "And we know...")],
            [Verse("barnes", Traditions.Evangelical, "x")], [], Fixtures.Catalog())!;

        Assert.StartsWith("VERSE TEXT:\n[KJV] Romans 8:28 — And we know...", ctx.Text.Replace("\r\n", "\n"));
        Assert.Single(ctx.Sources);
    }

    [Fact]
    public void OverBudget_DropsTheLowestRankedChunksFirst()
    {
        var chunks = Enumerable.Range(0, 5)
            .Select(i => new ScoredChunk(Fixtures.Chunk($"m{i}", Traditions.Evangelical, new string('x', 200) + i, index: i), 0.9f - i / 10f))
            .ToList();

        var ctx = ContextFormatter.Format([], chunks, [], Fixtures.Catalog(), maxChars: 600)!;

        Assert.InRange(ctx.Sources.Count, 1, 3);
        Assert.Equal("m0", ctx.Sources[0].ModuleId);           // best kept
        Assert.DoesNotContain(ctx.Sources, s => s.ModuleId == "m4"); // worst dropped
    }

    [Fact]
    public void Nothing_ReturnsNull()
    {
        Assert.Null(ContextFormatter.Format([], [], [], Fixtures.Catalog()));
    }
}
