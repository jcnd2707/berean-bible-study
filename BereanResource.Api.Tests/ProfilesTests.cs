using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BereanResourceApi.Models;

namespace BereanResource.Api.Tests;

public class ProfilesTests(SamplesApiFactory factory) : IClassFixture<SamplesApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_ThenList_ThenRename()
    {
        var created = await Create("Alice-" + Guid.NewGuid());

        var list = await _client.GetFromJsonAsync<List<ProfileRecord>>("/api/profiles", SamplesApiFactory.Json);
        Assert.Contains(list!, p => p.Id == created.Id && p.Name == created.Name);

        var renameResponse = await _client.PutAsJsonAsync(
            $"/api/profiles/{created.Id}", new RenameProfileRequest("Alicia"), SamplesApiFactory.Json);
        renameResponse.EnsureSuccessStatusCode();

        var renamed = await renameResponse.Content.ReadFromJsonAsync<ProfileRecord>(SamplesApiFactory.Json);
        Assert.Equal("Alicia", renamed!.Name);

        var fetched = await _client.GetFromJsonAsync<ProfileRecord>($"/api/profiles/{created.Id}", SamplesApiFactory.Json);
        Assert.Equal("Alicia", fetched!.Name);
    }

    [Fact]
    public async Task UnknownId_Returns404()
    {
        var get = await _client.GetAsync($"/api/profiles/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var rename = await _client.PutAsJsonAsync(
            $"/api/profiles/{Guid.NewGuid():N}", new RenameProfileRequest("Nobody"), SamplesApiFactory.Json);
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
    }

    [Fact]
    public async Task Notes_AreIsolatedBetweenProfiles()
    {
        var a = await Create("A-" + Guid.NewGuid());
        var b = await Create("B-" + Guid.NewGuid());
        var reference = $"Test.{Guid.NewGuid():N}";

        (await UpsertNoteAs(a.Id, reference, "A's note")).EnsureSuccessStatusCode();

        var bReadsBeforeWriting = await GetNoteAs(b.Id, reference);
        Assert.Equal(HttpStatusCode.NotFound, bReadsBeforeWriting.StatusCode);

        (await UpsertNoteAs(b.Id, reference, "B's note")).EnsureSuccessStatusCode();

        var aNote = await (await GetNoteAs(a.Id, reference)).Content.ReadFromJsonAsync<NoteRecord>(SamplesApiFactory.Json);
        var bNote = await (await GetNoteAs(b.Id, reference)).Content.ReadFromJsonAsync<NoteRecord>(SamplesApiFactory.Json);
        Assert.Equal("A's note", aNote!.Text);
        Assert.Equal("B's note", bNote!.Text);

        Assert.Contains(await ListNotesAs(a.Id), n => n.Reference == reference);
        Assert.Contains(await ListNotesAs(b.Id), n => n.Reference == reference);
    }

    [Fact]
    public async Task Notes_WithoutAProfileHeader_Returns400()
    {
        var response = await _client.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Notes_WithAnUnknownProfileHeader_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
        request.Headers.Add("X-Berean-Profile", Guid.NewGuid().ToString("N"));
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BibleEndpoints_WorkWithoutAProfile()
    {
        var response = await _client.GetAsync("/api/resources/books");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Migration_MakesExistingNotesUnowned_AndAdoptWorks_AndDoesNotReMigrate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"berean-migration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "notes.db");
        var backupPath = dbPath + ".pre-profiles.bak";

        try
        {
            SeedPreProfilesDb(dbPath, "Gen.1.1", "In the beginning...");

            using (var migrationFactory = SamplesApiFactory.WithNotesDb(dbPath))
            {
                var client = migrationFactory.CreateClient(); // builds the host -> EnsureCreated -> migration runs
                Assert.True(File.Exists(backupPath));

                var unowned = await client.GetFromJsonAsync<JsonElement>("/api/profiles/unowned", SamplesApiFactory.Json);
                Assert.Equal(1, unowned.GetProperty("notes").GetInt32());

                var profile = await CreateOn(client, "Migrated-owner");

                var adopt = await client.PostAsync($"/api/profiles/{profile.Id}/adopt-unowned", null);
                adopt.EnsureSuccessStatusCode();

                var afterAdopt = await client.GetFromJsonAsync<JsonElement>("/api/profiles/unowned", SamplesApiFactory.Json);
                Assert.Equal(0, afterAdopt.GetProperty("notes").GetInt32());

                var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
                request.Headers.Add("X-Berean-Profile", profile.Id);
                var response = await client.SendAsync(request);
                var notes = await response.Content.ReadFromJsonAsync<List<NoteRecord>>(SamplesApiFactory.Json);
                Assert.Single(notes!, n => n.Reference == "Gen.1.1" && n.Text == "In the beginning...");
            }

            var backupWriteTimeAfterFirstStart = File.GetLastWriteTimeUtc(backupPath);

            using (var secondStart = SamplesApiFactory.WithNotesDb(dbPath))
            {
                _ = secondStart.CreateClient();
            }

            Assert.Equal(backupWriteTimeAfterFirstStart, File.GetLastWriteTimeUtc(backupPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ProfileRecord> Create(string name) => CreateOn(_client, name);

    private static async Task<ProfileRecord> CreateOn(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/profiles", new CreateProfileRequest(name), SamplesApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileRecord>(SamplesApiFactory.Json))!;
    }

    private Task<HttpResponseMessage> UpsertNoteAs(string profileId, string reference, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/notes/{reference}")
        {
            Content = JsonContent.Create(new UpsertNoteRequest(text), options: SamplesApiFactory.Json),
        };
        request.Headers.Add("X-Berean-Profile", profileId);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> GetNoteAs(string profileId, string reference)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/notes/{reference}");
        request.Headers.Add("X-Berean-Profile", profileId);
        return _client.SendAsync(request);
    }

    private async Task<List<NoteRecord>> ListNotesAs(string profileId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
        request.Headers.Add("X-Berean-Profile", profileId);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<NoteRecord>>(SamplesApiFactory.Json))!;
    }

    private static void SeedPreProfilesDb(string path, string reference, string text)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Notes (
                Reference TEXT PRIMARY KEY,
                Text      TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            INSERT INTO Notes (Reference, Text, CreatedAt, UpdatedAt) VALUES ($ref, $text, $now, $now);
            """;
        var now = DateTime.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("$ref", reference);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }
}
