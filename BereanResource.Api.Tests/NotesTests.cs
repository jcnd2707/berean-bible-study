using System.Net.Http.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class NotesTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Append_CreatesThenAddsToTheNote_WithoutReplacingIt()
    {
        var reference = $"Test.{Guid.NewGuid():N}";

        var first = await _client.PostAsJsonAsync($"/api/notes/{reference}/append",
            new UpsertNoteRequest("First line."), SamplesApiFactory.Json);
        first.EnsureSuccessStatusCode();
        var afterFirst = await first.Content.ReadFromJsonAsync<NoteRecord>(SamplesApiFactory.Json);
        Assert.Contains("First line.", afterFirst!.Text);

        var second = await _client.PostAsJsonAsync($"/api/notes/{reference}/append",
            new UpsertNoteRequest("Second line."), SamplesApiFactory.Json);
        second.EnsureSuccessStatusCode();
        var afterSecond = await second.Content.ReadFromJsonAsync<NoteRecord>(SamplesApiFactory.Json);

        Assert.Contains("First line.", afterSecond!.Text);
        Assert.Contains("Second line.", afterSecond.Text);

        var fetched = await _client.GetFromJsonAsync<NoteRecord>($"/api/notes/{reference}", SamplesApiFactory.Json);
        Assert.Equal(afterSecond.Text, fetched!.Text);
    }

    [Fact]
    public async Task Append_WithEmptyText_IsRejected()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/notes/Test.{Guid.NewGuid():N}/append", new UpsertNoteRequest(""), SamplesApiFactory.Json);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
