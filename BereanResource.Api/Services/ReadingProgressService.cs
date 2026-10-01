using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Which Bible chapters each profile has marked as read, stored in the same <c>notes.db</c> as
/// notes and profiles. Keyed by (profile, canonical book number 1-66, chapter) rather than by a
/// module's book abbreviation, so a chapter read in the KJV also shows as read in the ASV — see
/// CHAPTER_PROGRESS_PLAN.md D1.
/// </summary>
public class ReadingProgressService(IOptions<BereanResourcesConfig> config, ILogger<ReadingProgressService> logger)
{
    private readonly string _dbPath = config.Value.NotesDbPath;

    /// <summary>Ensures the read-chapters table exists. Call at startup, next to NotesService.EnsureCreated.</summary>
    public void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ReadChapters (
                ProfileId TEXT    NOT NULL,
                Book      INTEGER NOT NULL,
                Chapter   INTEGER NOT NULL,
                ReadAt    TEXT    NOT NULL,
                PRIMARY KEY (ProfileId, Book, Chapter)
            );
            """;
        cmd.ExecuteNonQuery();
        logger.LogInformation("Reading progress table ready at {Path}", _dbPath);
    }

    /// <summary>True when <paramref name="book"/> is 1-66 and <paramref name="chapter"/> exists in it.</summary>
    public static bool IsValidChapter(int book, int chapter) =>
        chapter >= 1 && chapter <= BookMapper.ChapterCount(book);

    public List<ReadChapterRecord> GetAll(string profileId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Book, Chapter, ReadAt FROM ReadChapters WHERE ProfileId = $p ORDER BY Book, Chapter";
        cmd.Parameters.AddWithValue("$p", profileId);

        using var reader = cmd.ExecuteReader();
        var chapters = new List<ReadChapterRecord>();
        while (reader.Read())
            chapters.Add(new ReadChapterRecord(reader.GetInt32(0), reader.GetInt32(1), DateTime.Parse(reader.GetString(2))));
        return chapters;
    }

    /// <summary>Marks a chapter read. Marking it again just refreshes when it was last confirmed read.</summary>
    public ReadChapterRecord MarkRead(string profileId, int book, int chapter)
    {
        var now = DateTime.UtcNow;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ReadChapters (ProfileId, Book, Chapter, ReadAt)
            VALUES ($p, $book, $chapter, $now)
            ON CONFLICT(ProfileId, Book, Chapter) DO UPDATE SET ReadAt = excluded.ReadAt;
            """;
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$book", book);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.ExecuteNonQuery();

        return new ReadChapterRecord(book, chapter, now);
    }

    public bool MarkUnread(string profileId, int book, int chapter)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ReadChapters WHERE ProfileId = $p AND Book = $book AND Chapter = $chapter";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$book", book);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        return cmd.ExecuteNonQuery() > 0;
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
