using System.Net;
using System.Net.Http.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class ReadingProgressTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private const string Chapters = "/api/progress/chapters";

    private readonly SamplesApiFactory _factory = factory;

    [Fact]
    public async Task MarkRead_ThenList_ThenUnmark()
    {
        using var client = await ClientForNewProfile();

        (await client.PutAsync($"{Chapters}/1/1", null)).EnsureSuccessStatusCode();
        (await client.PutAsync($"{Chapters}/43/3", null)).EnsureSuccessStatusCode();

        var listed = await List(client);
        Assert.Equal([(1, 1), (43, 3)], listed.Select(c => (c.Book, c.Chapter)));

        var unmark = await client.DeleteAsync($"{Chapters}/1/1");
        Assert.Equal(HttpStatusCode.NoContent, unmark.StatusCode);

        var afterUnmark = await List(client);
        Assert.Equal([(43, 3)], afterUnmark.Select(c => (c.Book, c.Chapter)));
    }

    [Fact]
    public async Task MarkingTwice_RefreshesReadAt_InsteadOfFailing()
    {
        using var client = await ClientForNewProfile();

        var first = await client.PutAsync($"{Chapters}/19/23", null);
        first.EnsureSuccessStatusCode();
        var firstReadAt = (await first.Content.ReadFromJsonAsync<ReadChapterRecord>(SamplesApiFactory.Json))!.ReadAt;

        await Task.Delay(20);

        var second = await client.PutAsync($"{Chapters}/19/23", null);
        second.EnsureSuccessStatusCode();
        var secondReadAt = (await second.Content.ReadFromJsonAsync<ReadChapterRecord>(SamplesApiFactory.Json))!.ReadAt;

        Assert.True(secondReadAt > firstReadAt);
        Assert.Single(await List(client));
    }

    [Theory]
    [InlineData(0, 1)]    // no book 0
    [InlineData(67, 1)]   // one past Revelation
    [InlineData(1, 0)]    // chapters start at 1
    [InlineData(1, 51)]   // Genesis has 50
    [InlineData(65, 2)]   // Jude has 1
    public async Task MarkRead_ForAChapterThatDoesNotExist_Returns404(int book, int chapter)
    {
        using var client = await ClientForNewProfile();

        var response = await client.PutAsync($"{Chapters}/{book}/{chapter}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await List(client));
    }

    [Fact]
    public async Task MarkRead_AcceptsTheFirstAndLastChapterOfTheBible()
    {
        using var client = await ClientForNewProfile();

        (await client.PutAsync($"{Chapters}/1/1", null)).EnsureSuccessStatusCode();
        (await client.PutAsync($"{Chapters}/66/22", null)).EnsureSuccessStatusCode();
        (await client.PutAsync($"{Chapters}/19/150", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Unmark_OfAChapterThatWasNeverMarked_Returns404()
    {
        using var client = await ClientForNewProfile();

        var response = await client.DeleteAsync($"{Chapters}/1/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Progress_IsIsolatedBetweenProfiles()
    {
        using var alice = await ClientForNewProfile();
        using var bob = await ClientForNewProfile();

        (await alice.PutAsync($"{Chapters}/1/1", null)).EnsureSuccessStatusCode();

        Assert.Single(await List(alice));
        Assert.Empty(await List(bob));

        // Bob unmarking a chapter only Alice has read must not touch Alice's mark.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"{Chapters}/1/1")).StatusCode);
        Assert.Single(await List(alice));
    }

    [Fact]
    public async Task WithoutAProfile_IsRejected()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Chapters)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync($"{Chapters}/1/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{Chapters}/1/1")).StatusCode);
    }

    private async Task<HttpClient> ClientForNewProfile()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/profiles", new CreateProfileRequest("Reader-" + Guid.NewGuid()), SamplesApiFactory.Json);
        var profile = await response.Content.ReadFromJsonAsync<ProfileRecord>(SamplesApiFactory.Json);
        client.DefaultRequestHeaders.Add("X-Berean-Profile", profile!.Id);
        return client;
    }

    private static async Task<List<ReadChapterRecord>> List(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ReadChapterRecord>>(Chapters, SamplesApiFactory.Json))!;
}
