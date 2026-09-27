using System.Net.Http.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class CommentaryTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("barnes")]
    [InlineData("clarke")]
    [InlineData("henry")]
    [InlineData("jfb")]
    public async Task GetChapter_ReturnsEntriesForEverySampledCommentary(string moduleId)
    {
        var response = await _client.GetAsync($"/api/commentary/{moduleId}/John/3");
        response.EnsureSuccessStatusCode();

        var chapter = await response.Content.ReadFromJsonAsync<CommentaryChapter>(SamplesApiFactory.Json);

        Assert.NotNull(chapter);
        Assert.Equal(moduleId, chapter!.ModuleId);
        Assert.NotEmpty(chapter.Entries);
        Assert.All(chapter.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
    }
}
