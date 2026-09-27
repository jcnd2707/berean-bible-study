using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Manages personal study notes stored in a dedicated SQLite file (not the e-Sword files).
/// Notes are keyed by a simple string reference e.g. "John 3:16".
/// </summary>
public class NotesService(IOptions<BereanResourcesConfig> config, ILogger<NotesService> logger)
{
    private readonly string _dbPath = config.Value.NotesDbPath;

    /// <summary>Ensures the notes database and table exist. Call at startup.</summary>
    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Notes (
                Reference TEXT PRIMARY KEY,
                Text      TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        logger.LogInformation("Notes database ready at {Path}", _dbPath);
    }

    public NoteRecord? Get(string reference)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Reference, Text, CreatedAt, UpdatedAt FROM Notes WHERE Reference = $ref";
        cmd.Parameters.AddWithValue("$ref", reference);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return Map(reader);
    }

    public List<NoteRecord> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Reference, Text, CreatedAt, UpdatedAt FROM Notes ORDER BY UpdatedAt DESC";

        using var reader = cmd.ExecuteReader();
        var notes = new List<NoteRecord>();
        while (reader.Read()) notes.Add(Map(reader));
        return notes;
    }

    public NoteRecord Upsert(string reference, string text)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var now = DateTime.UtcNow.ToString("O");

        cmd.CommandText = """
            INSERT INTO Notes (Reference, Text, CreatedAt, UpdatedAt)
            VALUES ($ref, $text, $now, $now)
            ON CONFLICT(Reference) DO UPDATE SET
                Text      = excluded.Text,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$ref", reference);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return Get(reference)!;
    }

    /// <summary>
    /// Adds text to the end of a note (creating it if needed), separated from what is already
    /// there, so saving something never overwrites what was written before.
    /// </summary>
    public NoteRecord Append(string reference, string text)
    {
        var existing = Get(reference);
        var combined = existing is null || string.IsNullOrWhiteSpace(existing.Text)
            ? text
            : existing.Text.TrimEnd() + "\n\n---\n\n" + text;
        return Upsert(reference, combined);
    }

    public bool Delete(string reference)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Notes WHERE Reference = $ref";
        cmd.Parameters.AddWithValue("$ref", reference);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        // Ensure the directory exists
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static NoteRecord Map(SqliteDataReader reader) =>
        new(
            Reference: reader.GetString(0),
            Text: reader.GetString(1),
            CreatedAt: DateTime.Parse(reader.GetString(2)),
            UpdatedAt: DateTime.Parse(reader.GetString(3))
        );
}