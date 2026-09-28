using System.Collections.Concurrent;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// "Who's studying?" profiles, stored in the same <c>notes.db</c> as personal notes. Not an auth
/// mechanism — see PROFILES_AND_SESSIONS_PLAN.md D1: profiles keep two people's data apart, they
/// don't stop anyone who can reach the API from reading or writing another profile's data.
/// </summary>
public class ProfileService(IOptions<BereanResourcesConfig> config, ILogger<ProfileService> logger)
{
    private readonly string _dbPath = config.Value.NotesDbPath;

    // Only positive results are cached: an id that doesn't exist yet (a typo, a stale header, or
    // someone probing) should never be remembered forever.
    private readonly ConcurrentDictionary<string, bool> _knownToExist = new();

    /// <summary>Ensures the profiles table exists. Call at startup, next to NotesService.EnsureCreated.</summary>
    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Profiles (
                Id        TEXT PRIMARY KEY,
                Name      TEXT NOT NULL,
                Color     TEXT,
                CreatedAt TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        logger.LogInformation("Profiles table ready at {Path}", _dbPath);
    }

    public List<ProfileRecord> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Color, CreatedAt FROM Profiles ORDER BY CreatedAt";
        using var reader = cmd.ExecuteReader();
        var list = new List<ProfileRecord>();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    public ProfileRecord? Get(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Color, CreatedAt FROM Profiles WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public ProfileRecord Create(string name, string? color = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Profiles (Id, Name, Color, CreatedAt) VALUES ($id, $name, $color, $now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        _knownToExist[id] = true;
        return new ProfileRecord(id, name, color, DateTime.Parse(now));
    }

    /// <summary>Returns the updated profile, or null if there is no such profile.</summary>
    public ProfileRecord? Rename(string id, string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Profiles SET Name = $name WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0 ? Get(id) : null;
    }

    /// <summary>Called on (almost) every request that carries a profile header, hence the cache.</summary>
    public bool Exists(string id)
    {
        if (_knownToExist.ContainsKey(id)) return true;

        var exists = Get(id) is not null;
        if (exists) _knownToExist[id] = true;
        return exists;
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static ProfileRecord Map(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Color: reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt: DateTime.Parse(reader.GetString(3))
        );
}
