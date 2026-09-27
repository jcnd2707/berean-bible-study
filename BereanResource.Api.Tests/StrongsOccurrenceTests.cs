using System.Net.Http.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class StrongsOccurrenceTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task FindsOccurrences_OfATagInTheSampledChapters()
    {
        // H430 (Elohim/God) appears in the sampled Genesis 1-2 and John 1 chapters.
        var response = await _client.GetAsync("/api/bible/akjvstrong/strongs/H430/occurrences");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<StrongsOccurrences>(SamplesApiFactory.Json);

        Assert.NotNull(result);
        Assert.Equal("H430", result!.Number);
        Assert.True(result.Count > 0);
        Assert.NotEmpty(result.ByBook);
        Assert.NotEmpty(result.Sample);
    }

    [Fact]
    public async Task RejectsAMalformedNumber()
    {
        var response = await _client.GetAsync("/api/bible/akjvstrong/strongs/not-a-number/occurrences");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AModuleWithoutStrongsTags_404s()
    {
        var response = await _client.GetAsync("/api/bible/ASV/strongs/H430/occurrences");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
