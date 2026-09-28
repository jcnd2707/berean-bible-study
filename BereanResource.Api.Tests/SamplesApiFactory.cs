using System.Text.Json;
using BereanResourceApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BereanResource.Api.Tests;

/// <summary>
/// Runs the real BereanResource.Api (unmodified) against the checked-in samples/ library, so these
/// tests exercise the actual controllers and services rather than mocks. Each instance gets its
/// own scratch notes.db (and the Strong's occurrence cache that rides next to it), deleted on
/// dispose, so tests never share or pollute state.
/// </summary>
public sealed class SamplesApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _scratchDir = Path.Combine(Path.GetTempPath(), $"berean-resource-api-tests-{Guid.NewGuid():N}");
    private readonly string? _explicitNotesDbPath;

    /// <summary>Where this instance's notes.db lives — the scratch one, unless a migration test pointed it elsewhere.</summary>
    public string NotesDbPath => _explicitNotesDbPath ?? Path.Combine(_scratchDir, "notes.db");

    /// <summary>xUnit requires a class fixture to have exactly one public constructor.</summary>
    public SamplesApiFactory() { }

    private SamplesApiFactory(string explicitNotesDbPath) => _explicitNotesDbPath = explicitNotesDbPath;

    /// <summary>For migration tests: points notes.db at an existing file instead of a fresh scratch one.</summary>
    public static SamplesApiFactory WithNotesDb(string path) => new(path);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var repoRoot = FindRepoRoot();
            Directory.CreateDirectory(_scratchDir);

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BereanResources:RootPath"] = Path.Combine(repoRoot, "samples"),
                ["BereanResources:NotesDbPath"] = NotesDbPath,
                ["BereanResources:CrossReferencesDbFolder"] = Path.Combine(repoRoot, "samples", "cross_references"),
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best effort */ }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Berean.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the solution root from " + AppContext.BaseDirectory);
    }
}
