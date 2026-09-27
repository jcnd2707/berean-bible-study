using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class BibleTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetChapter_ReturnsTheSampledVerses()
    {
        var response = await _client.GetAsync("/api/bible/ASV/John/3");
        response.EnsureSuccessStatusCode();

        var chapter = await response.Content.ReadFromJsonAsync<ChapterRecord>(SamplesApiFactory.Json);

        Assert.NotNull(chapter);
        Assert.Equal(43, chapter!.Book);
        Assert.NotEmpty(chapter.Verses);
        var verse16 = Assert.Single(chapter.Verses, v => v.Verse == 16);
        Assert.Contains("God so loved", verse16.Text);
    }

    [Fact]
    public async Task GetVerse_ReturnsExactText()
    {
        var response = await _client.GetAsync("/api/bible/BSB/Genesis/1/1");
        response.EnsureSuccessStatusCode();

        var verse = await response.Content.ReadFromJsonAsync<VerseRecord>(SamplesApiFactory.Json);

        Assert.NotNull(verse);
        Assert.Equal("In the beginning God created the heavens and the earth.", verse!.Text);
    }

    [Fact]
    public async Task GetChapter_OutsideTheSample_404s()
    {
        // Exodus isn't one of the sample's chapters.
        var response = await _client.GetAsync("/api/bible/ASV/Exodus/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JsonIsCamelCase()
    {
        var response = await _client.GetAsync("/api/bible/ASV/John/3");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("bookName", out _));
        Assert.False(doc.RootElement.TryGetProperty("BookName", out _));
    }
}
