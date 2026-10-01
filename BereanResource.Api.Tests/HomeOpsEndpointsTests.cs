using System.Net;
using System.Text.Json;

namespace BereanResource.Api.Tests;

/// <summary>/health and /version are what the HomeOps deploy module probes after every deploy.</summary>
public class HomeOpsEndpointsTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Version_ReturnsTheBuildVersion()
    {
        var response = await _client.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = doc.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("unknown", version);
    }
}
