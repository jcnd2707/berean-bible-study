using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Manages personal study notes stored in a dedicated SQLite file (not the e-Sword files).
/// Notes are keyed by (profile, reference), e.g. (Alice, "John 3:16"), so two profiles can each
/// have their own note on the same verse.
/// </summary>
public class NotesService(IOptions<BereanResourcesConfig> config, ILogger<NotesService> logger)
{
    /// <summary>Notes written before profiles existed live here until adopted (see AdoptUnowned).</summary>
    public const string Unowned = "";

    private readonly string _dbPath = config.Value.NotesDbPath;

    /// <summary>Ensures the notes database and table exist, migrating a pre-profiles database first. Call at startup.</summary>
    public void EnsureCreated()
    {
        MigrateToProfilesIfNeeded();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Notes (
                ProfileId TEXT NOT NULL,
                Reference TEXT NOT NULL,
                Text      TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Reference)
            );
            """;
        cmd.ExecuteNonQuery();
        logger.LogInformation("Notes database ready at {Path}", _dbPath);
    }

    public NoteRecord? Get(string profileId, string reference)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Reference, Text, CreatedAt, UpdatedAt FROM Notes WHERE ProfileId = $p AND Reference = $ref";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$ref", reference);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<NoteRecord> GetAll(string profileId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Reference, Text, CreatedAt, UpdatedAt FROM Notes WHERE ProfileId = $p ORDER BY UpdatedAt DESC";
        cmd.Parameters.AddWithValue("$p", profileId);

        using var reader = cmd.ExecuteReader();
        var notes = new List<NoteRecord>();
        while (reader.Read()) notes.Add(Map(reader));
        return notes;
    }

    public NoteRecord Upsert(string profileId, string reference, string text)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var now = DateTime.UtcNow.ToString("O");

        cmd.CommandText = """
            INSERT INTO Notes (ProfileId, Reference, Text, CreatedAt, UpdatedAt)
            VALUES ($p, $ref, $text, $now, $now)
            ON CONFLICT(ProfileId, Reference) DO UPDATE SET
                Text      = excluded.Text,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$ref", reference);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return Get(profileId, reference)!;
    }

    /// <summary>
    /// Adds text to the end of a note (creating it if needed), separated from what is already
    /// there, so saving something never overwrites what was written before.
    /// </summary>
    public NoteRecord Append(string profileId, string reference, string text)
    {
        var existing = Get(profileId, reference);
        var combined = existing is null || string.IsNullOrWhiteSpace(existing.Text)
            ? text
            : existing.Text.TrimEnd() + "\n\n---\n\n" + text;
        return Upsert(profileId, reference, combined);
    }

    public bool Delete(string profileId, string reference)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Notes WHERE ProfileId = $p AND Reference = $ref";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$ref", reference);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Adoption (profile picker's "keep what's already here?") ─────────────────

    public int CountUnowned()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Notes WHERE ProfileId = $unowned";
        cmd.Parameters.AddWithValue("$unowned", Unowned);
        return Convert.ToInt32((long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// Moves every unowned note to <paramref name="profileId"/>. A note the profile already has at
    /// the same reference is left unowned (OR IGNORE) rather than overwritten, so nothing is lost —
    /// the caller can check <see cref="CountUnowned"/> afterwards for anything left behind.
    /// Returns how many notes moved.
    /// </summary>
    public int AdoptUnowned(string profileId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OR IGNORE Notes SET ProfileId = $p WHERE ProfileId = $unowned";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$unowned", Unowned);
        return cmd.ExecuteNonQuery();
    }

    // ── Migration ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-time rebuild of a pre-profiles Notes table (Reference TEXT PRIMARY KEY) into the
    /// (ProfileId, Reference) schema, with existing rows becoming unowned. Backs up the database
    /// first, and only once — a second start finds the ProfileId column already there and does
    /// nothing.
    /// </summary>
    private void MigrateToProfilesIfNeeded()
    {
        if (!File.Exists(_dbPath)) return;

        using (var conn = Open())
        {
            if (!TableExists(conn, "Notes") || ColumnExists(conn, "Notes", "ProfileId")) return;
        }

        var backupPath = _dbPath + ".pre-profiles.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(_dbPath, backupPath);
            logger.LogInformation("Backed up pre-profiles notes database to {Path}", backupPath);
        }

        using var migrate = Open();
        using var tx = migrate.BeginTransaction();
        using (var cmd = migrate.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE Notes_new (
                    ProfileId TEXT NOT NULL,
                    Reference TEXT NOT NULL,
                    Text      TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (ProfileId, Reference)
                );
                INSERT INTO Notes_new (ProfileId, Reference, Text, CreatedAt, UpdatedAt)
                    SELECT '', Reference, Text, CreatedAt, UpdatedAt FROM Notes;
                DROP TABLE Notes;
                ALTER TABLE Notes_new RENAME TO Notes;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        logger.LogInformation("Migrated notes to the per-profile schema — existing notes are unowned until adopted");
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return (long)cmd.ExecuteScalar()! > 0;
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
