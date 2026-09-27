using Xunit;

namespace Berean.Core.Tests;

public class QueryRouterParsingTests
{
    [Theory]
    [InlineData("John 3:16", 43, 3, 16, null)]
    [InlineData("What does John 3:16 mean?", 43, 3, 16, null)]
    [InlineData("Explain John 1:1.", 43, 1, 1, null)]
    [InlineData("Tell me about Romans 8:28", 45, 8, 28, null)]
    [InlineData("1 John 4:8", 62, 4, 8, null)]
    [InlineData("Explain 1 John 4:8", 62, 4, 8, null)]
    [InlineData("Romans 8:28-30", 45, 8, 28, 30)]
    [InlineData("Mat 24:34", 40, 24, 34, null)]
    [InlineData("Juan 3:16", 43, 3, 16, null)]
    [InlineData("Song of Solomon 2:3", 22, 2, 3, null)]
    [InlineData("Consider Luke 16:19-31 and Eccl 9:5", 42, 16, 19, 31)]
    public void TryParseVerse_ParsesReferences(string query, int book, int chapter, int verse, int? verseEnd)
    {
        var r = QueryRouter.TryParseVerse(query);

        Assert.NotNull(r);
        Assert.Equal(book, r!.BookNumber);
        Assert.Equal(chapter, r.Chapter);
        Assert.Equal(verse, r.Verse);
        Assert.Equal(verseEnd, r.VerseEnd);
    }

    [Fact]
    public void TryParseVerse_ChapterOnly_HasNoVerse()
    {
        var r = QueryRouter.TryParseVerse("Psalm 23");

        Assert.NotNull(r);
        Assert.Equal(19, r!.BookNumber);
        Assert.Equal(23, r.Chapter);
        Assert.Null(r.Verse);
    }

    [Fact]
    public void TryParseVerse_DiscreteList()
    {
        var r = QueryRouter.TryParseVerse("John 3:16,18");

        Assert.NotNull(r);
        Assert.Equal([16, 18], r!.Verses);
    }

    [Theory]
    [InlineData("what does agape mean")]
    [InlineData("G25")]
    [InlineData("H430")]
    [InlineData("what is in 2 weeks")]
    [InlineData("the 7 trumpets")]
    [InlineData("what happens when we die")]
    public void TryParseVerse_RejectsNonVerses(string query)
    {
        Assert.Null(QueryRouter.TryParseVerse(query));
    }

    [Fact]
    public void TryParseVerse_IgnoresContextTags()
    {
        // The client prefixes "[Passage: John chapter 3]"-style tags; only the question counts.
        var r = QueryRouter.TryParseVerse("[Translation: KJV]\n[Passage: Genesis chapter 1]\n\nWhat does the Bible say about grace?");

        Assert.Null(r);
    }

    [Theory]
    [InlineData("what does agape mean", true)]
    [InlineData("define pneuma", true)]
    [InlineData("G25", true)]
    [InlineData("H430", true)]
    [InlineData("¿qué significa ágape?", true)]
    [InlineData("does the soul survive death", false)]
    [InlineData("John 3:16", false)]
    public void IsDefinitionQuery_Classifies(string query, bool expected)
    {
        Assert.Equal(expected, QueryRouter.IsDefinitionQuery(query));
    }

    [Theory]
    [InlineData("What does G3340 mean?", "G3340")]
    [InlineData("What does the Hebrew word hesed mean?", "hesed")]
    [InlineData("What does nephesh mean in the Old Testament?", "nephesh")]
    [InlineData("define pneuma", "pneuma")]
    [InlineData("What is the meaning of shalom", "shalom")]
    public void ExtractLookupTerms_FindsTheWord(string query, string expected)
    {
        Assert.Contains(expected, QueryRouter.ExtractLookupTerms(query), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("What is the millennium of Revelation 20?")]
    [InlineData("What does the Bible teach about the state of the dead?")]
    public void ExtractLookupTerms_EmptyForBroadQuestions(string query)
    {
        Assert.Empty(QueryRouter.ExtractLookupTerms(query));
    }

    [Fact]
    public void QueryMessage_SplitsTagsFromQuestion()
    {
        var m = QueryRouter.QueryMessage.Parse(
            "[Translation: KJV]\n[Passage: John chapter 3]\n[Selected verse: John 3:16]\n\nWhy did he come?");

        Assert.Equal("Why did he come?", m.Question);
        Assert.Equal("John 3:16", m.SelectedVerse);
    }

    // The abbreviations BereanResource.Api gives every book (/api/bible/{module}/books). The web client
    // writes them into its context tags ("[Selected verse: Jhn 3:16]"), so the router has to know all 66.
    private static readonly string[] ApiAbbreviations =
    [
        "Gen", "Exo", "Lev", "Num", "Deu", "Jos", "Jdg", "Rut", "1Sa", "2Sa", "1Ki", "2Ki", "1Ch", "2Ch", "Ezr", "Neh",
        "Est", "Job", "Psa", "Pro", "Ecc", "Sng", "Isa", "Jer", "Lam", "Eze", "Dan", "Hos", "Joe", "Amo", "Oba", "Jon",
        "Mic", "Nah", "Hab", "Zep", "Hag", "Zec", "Mal", "Mat", "Mrk", "Luk", "Jhn", "Act", "Rom", "1Co", "2Co", "Gal",
        "Eph", "Php", "Col", "1Th", "2Th", "1Ti", "2Ti", "Tit", "Phm", "Heb", "Jas", "1Pe", "2Pe", "1Jn", "2Jn", "3Jn",
        "Jud", "Rev",
    ];

    [Fact]
    public void EveryApiBookAbbreviation_IsUnderstood_InAReference()
    {
        var failures = new List<string>();
        for (var i = 0; i < ApiAbbreviations.Length; i++)
        {
            // A plain reference; the tag form is parsed the same way once the tags are split off.
            var parsed = QueryRouter.TryParseVerse($"Explain {ApiAbbreviations[i]} 3:16");
            if (parsed is null || parsed.BookNumber != i + 1)
                failures.Add($"{ApiAbbreviations[i]} (book {i + 1}) → {parsed?.BookNumber.ToString() ?? "no match"}");
        }

        Assert.True(failures.Count == 0, "Not understood: " + string.Join("; ", failures));
    }

    [Theory]
    [InlineData("1 Jn 4:8", 62)]
    [InlineData("1Jn 4:8", 62)]
    [InlineData("2 Co 5:17", 47)]
    [InlineData("Song of Songs 2:3", 22)]
    public void SpacedAndCompactAbbreviations_MeanTheSameBook(string text, int book)
    {
        Assert.Equal(book, QueryRouter.TryParseVerse(text)?.BookNumber);
    }

    [Fact]
    public void BibleBookMap_GetFullName_IsTitleCase()
    {
        Assert.Equal("John", BibleBookMap.GetFullName(43));
        Assert.Equal("1 John", BibleBookMap.GetFullName(62));
        Assert.Equal("Song of Solomon", BibleBookMap.GetFullName(22));
    }
}
