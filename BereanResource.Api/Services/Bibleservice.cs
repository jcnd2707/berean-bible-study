using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Reads Bible text from scrollmapper-format SQLite databases.
///
/// Scrollmapper schema per file:
///   TABLE {translation}_books  — id (1-66), name (TEXT)
///   TABLE {translation}_verses — id, book_id, chapter, verse, text (TEXT, plain)
///   TABLE translations         — translation, title, license
///
/// The translation abbreviation is stored in the translations table and
/// used as the prefix for the books/verses table names (e.g. KJV_books, KJV_verses).
/// </summary>
public class BibleService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<BibleService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    /// <summary>Returns all books present in the module.</summary>
    public List<BookInfo> GetBooks(string moduleId, string language = "en")
    {
        var (path, translation) = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM [{translation}_books] ORDER BY id";

        using var reader = cmd.ExecuteReader();
        var books = new List<BookInfo>();
        while (reader.Read())
        {
            var number = reader.GetInt32(0);
            books.Add(new BookInfo(
                Number: number,
                Name: BookMapper.ToName(number, language),
                Abbreviation: BookMapper.ToAbbreviation(number, language),
                ChapterCount: BookMapper.ChapterCount(number)
            ));
        }

        return books;
    }

    /// <summary>Returns all verses in a chapter.</summary>
    public ChapterRecord? GetChapter(string moduleId, string bookName, int chapter, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var (path, translation) = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            SELECT book_id, chapter, verse, text
            FROM   [{translation}_verses]
            WHERE  book_id = $book
              AND  chapter = $chapter
            ORDER  BY verse
            """;
        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);

        using var reader = cmd.ExecuteReader();
        var verses = new List<VerseRecord>();

        while (reader.Read())
        {
            var b = reader.GetInt32(0);
            var ch = reader.GetInt32(1);
            var v = reader.GetInt32(2);
            var text = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();

            verses.Add(new VerseRecord(
                Book: b,
                Chapter: ch,
                Verse: v,
                BookName: BookMapper.ToName(b, language),
                Reference: BookMapper.ToReference(b, ch, v, language: language),
                Text: text
            ));
        }

        if (verses.Count == 0) return null;

        return new ChapterRecord(
            Book: bookNumber,
            BookName: BookMapper.ToName(bookNumber, language),
            Chapter: chapter,
            ModuleId: moduleId,
            Verses: verses
        );
    }

    /// <summary>Returns a single verse.</summary>
    public VerseRecord? GetVerse(string moduleId, string bookName, int chapter, int verse, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var (path, translation) = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            SELECT book_id, chapter, verse, text
            FROM   [{translation}_verses]
            WHERE  book_id = $book
              AND  chapter = $chapter
              AND  verse   = $verse
            """;
        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$verse", verse);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var b = reader.GetInt32(0);
        var ch = reader.GetInt32(1);
        var v = reader.GetInt32(2);
        var text = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();

        return new VerseRecord(
            Book: b,
            Chapter: ch,
            Verse: v,
            BookName: BookMapper.ToName(b, language),
            Reference: BookMapper.ToReference(b, ch, v, language: language),
            Text: text
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a moduleId to a file path and the exact translation abbreviation.
    /// The abbreviation is read from the translations table because casing must
    /// match the table name prefix exactly (e.g. "KJV" not "kjv").
    /// </summary>
    private (string Path, string Translation) ResolveOrThrow(string moduleId)
    {
        var path = discovery.ResolvePath(_cfg.SubFolders.Bibles, moduleId, ".db");
        if (path is null)
            throw new FileNotFoundException($"Bible module '{moduleId}' not found.");

        var translation = ReadTranslationAbbreviation(path, moduleId);
        return (path, translation);
    }

    private string ReadTranslationAbbreviation(string path, string fallback)
    {
        try
        {
            using var conn = Open(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT translation FROM translations LIMIT 1";
            var result = cmd.ExecuteScalar();
            return result?.ToString() ?? fallback;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read translation abbreviation from {Path}, falling back to '{Fallback}'", path, fallback);
            return fallback;
        }
    }

    private static int ResolveBookOrThrow(string bookName)
    {
        var number = BookMapper.ToNumber(bookName);
        if (number is null)
            throw new ArgumentException($"Unknown book name: '{bookName}'");
        return number.Value;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}