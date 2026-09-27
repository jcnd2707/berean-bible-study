using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

public class VectorStoreTests
{
    private static readonly float[] SabbathQuery = FakeEmbedder.Vector("sabbath");

    [Fact]
    public async Task Search_ExcludeTraditions_NeverReturnsThem()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
        [
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath rest"),
            Fixtures.Chunk("sdabc", Traditions.Adventist, "sabbath is the seventh day"),
            Fixtures.Chunk("egw", Traditions.Adventist, "sabbath sabbath", SourceType.Book, source: "The Desire of Ages, Chapter 1 — X"),
        ]);

        var hits = store.Search(SabbathQuery, new RetrievalFilter { ExcludeTraditions = [Traditions.Adventist] });

        Assert.Single(hits);
        Assert.Equal("barnes", hits[0].Chunk.ModuleId);
    }

    [Fact]
    public async Task Search_IncludeTraditions_ReturnsOnlyThose()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
        [
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath rest"),
            Fixtures.Chunk("sdabc", Traditions.Adventist, "sabbath is the seventh day"),
        ]);

        var hits = store.Search(SabbathQuery, new RetrievalFilter { IncludeTraditions = [Traditions.Adventist] });

        Assert.Equal(["sdabc"], hits.Select(h => h.Chunk.ModuleId));
    }

    [Fact]
    public async Task Search_MaxPerModule_SkipsToOtherModulesInsteadOfShrinking()
    {
        using var db = new TempDb();
        // Henry has five strong matches; Barnes and Clarke have one weaker one each.
        var chunks = Enumerable.Range(0, 5)
            .Select(i => Fixtures.Chunk("henry", Traditions.Reformed, $"sabbath sabbath note {i} death", index: i))
            .Concat([
                Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath death grace"),
                Fixtures.Chunk("clarke", Traditions.Wesleyan, "sabbath death grace"),
            ]).ToList();
        var (_, store) = await Fixtures.PipelineAsync(db.Path, chunks);

        var hits = store.Search(SabbathQuery, new RetrievalFilter { MaxPerModule = 2 }, topK: 4, lambda: 1f);

        Assert.Equal(4, hits.Count);
        Assert.True(hits.Count(h => h.Chunk.ModuleId == "henry") <= 2);
        Assert.Contains(hits, h => h.Chunk.ModuleId == "barnes");
        Assert.Contains(hits, h => h.Chunk.ModuleId == "clarke");
    }

    [Fact]
    public async Task Search_ExistingPerModule_CountsTowardTheCap()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
        [
            Fixtures.Chunk("henry", Traditions.Reformed, "sabbath one", index: 0),
            Fixtures.Chunk("henry", Traditions.Reformed, "sabbath two", index: 1),
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath three"),
        ]);

        var hits = store.Search(SabbathQuery, new RetrievalFilter
        {
            MaxPerModule = 2,
            ExistingPerModule = new Dictionary<string, int> { ["henry"] = 2 },
        });

        Assert.Equal(["barnes"], hits.Select(h => h.Chunk.ModuleId));
    }

    [Fact]
    public async Task Search_MinScore_DropsWeakMatches()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
        [
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath"),
            Fixtures.Chunk("clarke", Traditions.Wesleyan, "grace and death"),
        ]);

        var hits = store.Search(SabbathQuery, new RetrievalFilter { MinScore = 0.9f });

        Assert.Equal(["barnes"], hits.Select(h => h.Chunk.ModuleId));
    }

    [Fact]
    public async Task Search_UntaggedChunks_CountAsUnclassified()
    {
        using var db = new TempDb();
        var untagged = Fixtures.Chunk("mystery", Traditions.Evangelical, "sabbath");
        untagged.Tradition = null;
        var (_, store) = await Fixtures.PipelineAsync(db.Path, [untagged]);

        Assert.Single(store.Search(SabbathQuery, new RetrievalFilter { ExcludeTraditions = [Traditions.Adventist] }));
        Assert.Empty(store.Search(SabbathQuery, new RetrievalFilter { IncludeTraditions = [Traditions.Adventist] }));
    }

    [Fact]
    public async Task SearchByVerse_MatchesOverlappingRanges()
    {
        using var db = new TempDb();
        var inside = Fixtures.Chunk("barnes", Traditions.Evangelical, "a", book: 43, chapter: 3, verseBegin: 16, verseEnd: 16);
        var overlapping = Fixtures.Chunk("clarke", Traditions.Wesleyan, "b", book: 43, chapter: 3, verseBegin: 17, verseEnd: 21);
        var outside = Fixtures.Chunk("henry", Traditions.Reformed, "c", book: 43, chapter: 3, verseBegin: 1, verseEnd: 5);
        var (_, store) = await Fixtures.PipelineAsync(db.Path, [inside, overlapping, outside]);

        var hits = store.SearchByVerse(43, 3, 16, 18);

        Assert.Equal(["barnes", "clarke"], hits.Select(h => h.ModuleId).OrderBy(x => x));
    }

    [Fact]
    public async Task Backfill_TagsLegacyRows_WithoutTouchingEmbeddings()
    {
        using var db = new TempDb();
        var legacyCommentary = Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath", index: 0);
        legacyCommentary.ModuleId = null; legacyCommentary.Tradition = null;
        var legacyBook = Fixtures.Chunk("x", Traditions.Adventist, "death", SourceType.Book,
            source: "The Desire of Ages, Chapter 12 — The Sabbath");
        legacyBook.ModuleId = null; legacyBook.Tradition = null;
        var unknown = Fixtures.Chunk("mystery", Traditions.Adventist, "grace", source: "mystery.cmtx");
        unknown.ModuleId = null; unknown.Tradition = null;
        var (_, store) = await Fixtures.PipelineAsync(db.Path, [legacyCommentary, legacyBook, unknown]);

        var report = await store.BackfillTraditionsAsync(Fixtures.Catalog());
        await store.LoadAsync();

        Assert.Equal(2, report.RowsUpdated);
        Assert.Equal(1, report.ChunksByTradition[Traditions.Evangelical]);
        Assert.Equal(1, report.ChunksByTradition[Traditions.Adventist]);
        Assert.Contains(report.UnmatchedSources, s => s.Contains("mystery.cmtx"));

        var hits = store.Search(FakeEmbedder.Vector("sabbath"), new RetrievalFilter { IncludeTraditions = [Traditions.Evangelical] });
        Assert.Equal("barnes", hits.Single().Chunk.ModuleId);
        Assert.Equal(FakeEmbedder.Vector("sabbath"), hits.Single().Chunk.Embedding);
    }

    [Fact]
    public async Task Backfill_BookTitlePrefix_DoesNotMatchSimilarTitles()
    {
        using var db = new TempDb();
        // "The Desire of Ages, Chapter " must not match a title that merely starts the same way,
        // nor treat % or _ in a title as wildcards.
        var other = Fixtures.Chunk("x", Traditions.Adventist, "death", SourceType.Book,
            source: "The Desire of Ages Companion, Chapter 1 — X");
        other.ModuleId = null; other.Tradition = null;
        var (_, store) = await Fixtures.PipelineAsync(db.Path, [other]);

        var report = await store.BackfillTraditionsAsync(Fixtures.Catalog());

        Assert.Equal(0, report.RowsUpdated);
    }

    [Fact]
    public async Task Backfill_RetagsWhenAProfileChanges()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
            [Fixtures.Chunk("barnes", Traditions.Reformed, "sabbath")]); // stored as Reformed

        var report = await store.BackfillTraditionsAsync(Fixtures.Catalog()); // catalog says Evangelical

        Assert.Equal(1, report.RowsUpdated);
    }

    [Fact]
    public async Task Initialise_MigratesAnOldIndex_AndBacksItUpFirst()
    {
        using var db = new TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(db.Path)!);

        // An index as it was before the tradition columns existed.
        await using (var conn = new SqliteConnection($"Data Source={db.Path};"))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Chunks (
                    Id TEXT NOT NULL PRIMARY KEY, Source TEXT NOT NULL, ChunkIndex INTEGER NOT NULL,
                    Text TEXT NOT NULL, Embedding BLOB NOT NULL, BookNumber INTEGER, ChapterBegin INTEGER,
                    VerseBegin INTEGER, VerseEnd INTEGER, SourceType INTEGER NOT NULL DEFAULT 0,
                    Language TEXT NOT NULL DEFAULT 'en');
                INSERT INTO Chunks (Id, Source, ChunkIndex, Text, Embedding, SourceType)
                VALUES ('a::0', 'barnes', 0, 'sabbath', x'0000803F', 2);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new SqliteVectorStore(db.Path, NullLogger<SqliteVectorStore>.Instance);
        await store.InitialiseAsync();

        Assert.True(File.Exists(db.Path + ".pre-tradition.bak"));
        Assert.True(store.CompletionMarkersCreated);

        var report = await store.BackfillTraditionsAsync(Fixtures.Catalog());
        Assert.Equal(1, report.RowsUpdated);

        await store.SeedCompletionMarkersAsync();
        Assert.Contains("barnes", await store.CompletedModuleIdsAsync(SourceType.Commentary));
    }

    [Fact]
    public async Task ResetModule_RemovesPartialRows_AndMarker()
    {
        using var db = new TempDb();
        var (_, store) = await Fixtures.PipelineAsync(db.Path,
        [
            Fixtures.Chunk("barnes", Traditions.Evangelical, "sabbath"),
            Fixtures.Chunk("clarke", Traditions.Wesleyan, "grace"),
        ]);
        await store.MarkModuleCompleteAsync(SourceType.Commentary, "barnes");

        await store.ResetModuleAsync(SourceType.Commentary, "barnes");
        await store.LoadAsync();

        Assert.DoesNotContain("barnes", await store.CompletedModuleIdsAsync(SourceType.Commentary));
        Assert.Equal(["clarke"], store.IndexedModuleIds(SourceType.Commentary));
    }

    [Fact]
    public void DocumentChunker_GivesEveryEntryUniqueIds_WhenAPrefixIsSupplied()
    {
        var a = DocumentChunker.Chunk("first entry", "barnes", idPrefix: "barnes:43:3:1");
        var b = DocumentChunker.Chunk("second entry", "barnes", idPrefix: "barnes:43:3:2");

        Assert.Empty(a.Select(c => c.Id).Intersect(b.Select(c => c.Id)));
    }
}
