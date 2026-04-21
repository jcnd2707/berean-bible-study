using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Reads Bible text from two supported SQLite formats:
///
///   Scrollmapper (.db)
///     TABLE {translation}_books  — id, name
///     TABLE {translation}_verses — id, book_id, chapter, verse, text
///     TABLE translations         — translation, title, license
///
///   MySword (.bbl)
///     TABLE Bible   — Book, Chapter, Verse, Scripture
///     TABLE Details — Title, Abbreviation, Strong (bool), Language, …
///
/// Strong's tags are parsed by StrongsParser and exposed as StrongsWords
/// on each VerseRecord. Modules without tags return StrongsWords = null.
/// </summary>
public class BibleService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<BibleService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    // ── Public API ────────────────────────────────────────────────────────────

    public TranslationInfo? GetTranslationInfo(string moduleId)
    {
        var (path, format) = ResolvePathOrThrow(moduleId);
        return format == BibleFormat.MySword
            ? ReadMySwordInfo(path)
            : ReadScrollmapperInfo(path);
    }

    public List<BookInfo> GetBooks(string moduleId, string language = "en")
    {
        var (path, format) = ResolvePathOrThrow(moduleId);

        using var conn = Open(path);
        var bookIds = format == BibleFormat.MySword
            ? GetMySwordBookIds(conn)
            : GetScrollmapperBookIds(conn, ReadTranslation(path, moduleId));

        return bookIds.Select(number => new BookInfo(
            Number: number,
            Name: BookMapper.ToName(number, language),
            Abbreviation: BookMapper.ToAbbreviation(number, language),
            ChapterCount: BookMapper.ChapterCount(number)
        )).ToList();
    }

    public ChapterRecord? GetChapter(
        string moduleId, string bookName, int chapter, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var (path, format) = ResolvePathOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        if (format == BibleFormat.MySword)
        {
            cmd.CommandText = """
                SELECT Book, Chapter, Verse, Scripture
                FROM   Bible
                WHERE  Book    = $book
                  AND  Chapter = $chapter
                ORDER  BY Verse
                """;
        }
        else
        {
            var translation = ReadTranslation(path, moduleId);
            cmd.CommandText = $"""
                SELECT book_id, chapter, verse, text
                FROM   [{translation}_verses]
                WHERE  book_id = $book
                  AND  chapter = $chapter
                ORDER  BY verse
                """;
        }

        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);

        using var reader = cmd.ExecuteReader();
        var verses = new List<VerseRecord>();
        while (reader.Read())
            verses.Add(ReadVerseRecord(reader, language, format));

        if (verses.Count == 0) return null;

        return new ChapterRecord(
            Book: bookNumber,
            BookName: BookMapper.ToName(bookNumber, language),
            Chapter: chapter,
            ModuleId: moduleId,
            Verses: verses
        );
    }

    public VerseRecord? GetVerse(
        string moduleId, string bookName, int chapter, int verse, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var (path, format) = ResolvePathOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        if (format == BibleFormat.MySword)
        {
            cmd.CommandText = """
                SELECT Book, Chapter, Verse, Scripture
                FROM   Bible
                WHERE  Book    = $book
                  AND  Chapter = $chapter
                  AND  Verse   = $verse
                """;
        }
        else
        {
            var translation = ReadTranslation(path, moduleId);
            cmd.CommandText = $"""
                SELECT book_id, chapter, verse, text
                FROM   [{translation}_verses]
                WHERE  book_id = $book
                  AND  chapter = $chapter
                  AND  verse   = $verse
                """;
        }

        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$verse", verse);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return ReadVerseRecord(reader, language, format);
    }

    /// <summary>
    /// Full-text search across the entire module (or scoped by testament / book).
    /// Results are ordered canonically: book → chapter → verse.
    /// Text is stripped of Strong's/HTML markup before matching and returning,
    /// consistent with how GetChapter and GetVerse behave.
    /// </summary>
    /// <param name="moduleId">Module identifier, e.g. "BSB" or "KJV".</param>
    /// <param name="query">Case-insensitive substring to find (min 2 chars, enforced by controller).</param>
    /// <param name="limit">Max results to return (1–200, enforced by controller).</param>
    /// <param name="testament">"OT", "NT", or "BOTH" (already uppercased by controller).</param>
    /// <param name="bookAbbrev">Optional book abbreviation to scope results, e.g. "Rom".</param>
    /// <param name="language">UI language for book names / references.</param>
    public List<VerseRecord> Search(
        string moduleId,
        string query,
        int limit,
        string testament,
        string? bookAbbrev,
        string language = "en")
    {
        // ── Resolve optional book scope ───────────────────────────────────────
        int? bookNumber = null;
        if (!string.IsNullOrWhiteSpace(bookAbbrev))
        {
            bookNumber = BookMapper.ToNumber(bookAbbrev);
            if (bookNumber is null)
                throw new ArgumentException($"Unknown book abbreviation: '{bookAbbrev}'");
        }

        var (path, format) = ResolvePathOrThrow(moduleId);

        using var conn = Open(path);

        // SQLite's LIKE is case-insensitive for ASCII by default, but state it
        // explicitly so behaviour is predictable regardless of compile options.
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA case_sensitive_like = OFF;";
            pragma.ExecuteNonQuery();
        }

        // ── Build format-specific SQL ─────────────────────────────────────────
        //
        // Both queries return columns in the same order — book, chapter, verse, text —
        // so ReadVerseRecord can be reused without modification.
        //
        // Important: LIKE runs against the *raw* stored text (which may contain
        // Strong's tags). We therefore strip markup AFTER fetching, and then
        // filter client-side to drop any false positives caused by tag content
        // matching the query (e.g. searching "WG" would otherwise hit Strong tags).
        // For the vast majority of natural-language searches this is invisible;
        // for tag-heavy queries it keeps results clean.

        using var cmd = conn.CreateCommand();

        if (format == BibleFormat.MySword)
        {
            var conditions = BuildTestamentAndBookConditions(
                "Book", testament, bookNumber, cmd, isMySword: true);

            cmd.CommandText = $"""
                SELECT Book, Chapter, Verse, Scripture
                FROM   Bible
                {(conditions.Length > 0 ? "WHERE " + conditions : "")}
                ORDER  BY Book, Chapter, Verse
                LIMIT  $limit
                """;
        }
        else
        {
            var translation = ReadTranslation(path, moduleId);

            // For Scrollmapper we still apply LIKE on the raw text column to let
            // SQLite do the heavy lifting, then re-check against plain text below.
            var conditions = BuildTestamentAndBookConditions(
                "book_id", testament, bookNumber, cmd, isMySword: false);

            // Always add the LIKE filter for Scrollmapper (raw column search)
            var likeClause = conditions.Length > 0
                ? $"{conditions} AND text LIKE $pattern"
                : "text LIKE $pattern";

            cmd.CommandText = $"""
                SELECT book_id, chapter, verse, text
                FROM   [{translation}_verses]
                WHERE  {likeClause}
                ORDER  BY book_id, chapter, verse
                LIMIT  $limit
                """;
        }

        // Pattern parameter — used by Scrollmapper path and optionally MySword
        cmd.Parameters.AddWithValue("$pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        // ── Execute and map ───────────────────────────────────────────────────
        using var reader = cmd.ExecuteReader();
        var results = new List<VerseRecord>();

        while (reader.Read())
        {
            var record = ReadVerseRecord(reader, language, format);

            // For MySword, SQLite LIKE wasn't applied above (markup may interfere),
            // so we do the plain-text match here after stripping.
            if (format == BibleFormat.MySword &&
                !record.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            // Secondary guard for Scrollmapper: confirm match survives markup stripping.
            if (format == BibleFormat.Scrollmapper &&
                !record.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(record);

            if (results.Count >= limit) break;
        }

        return results;
    }

    // ── Format detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves moduleId to a file path and detects the format.
    /// Tries .bbl (MySword) first, then .db (Scrollmapper).
    /// </summary>
    private (string Path, BibleFormat Format) ResolvePathOrThrow(string moduleId)
    {
        var bbl = discovery.ResolvePath(_cfg.SubFolders.Bibles, moduleId, ".bbl");
        if (bbl is not null) return (bbl, BibleFormat.MySword);

        var db = discovery.ResolvePath(_cfg.SubFolders.Bibles, moduleId, ".db");
        if (db is not null) return (db, BibleFormat.Scrollmapper);

        throw new FileNotFoundException($"Bible module '{moduleId}' not found.");
    }

    // ── Scrollmapper helpers ──────────────────────────────────────────────────

    private TranslationInfo? ReadScrollmapperInfo(string path)
    {
        try
        {
            using var conn = Open(path);
            string translation;
            string? title, license;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT translation, title, license FROM translations LIMIT 1";
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                translation = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                title = r.IsDBNull(1) ? null : r.GetString(1).TrimStart('#', ' ');
                license = r.IsDBNull(2) ? null : r.GetString(2);
            }

            var hasStrongs = ProbeScrollmapperStrongs(conn, translation);
            return new TranslationInfo(translation, title, license, hasStrongs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read Scrollmapper metadata from '{Path}'", path);
            return null;
        }
    }

    private string ReadTranslation(string path, string fallback)
    {
        try
        {
            using var conn = Open(path);
            if (!TableExists(conn, "translations"))
                return DiscoverScrollmapperTranslation(conn, fallback);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT translation FROM translations LIMIT 1";
            return cmd.ExecuteScalar()?.ToString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // When the translations table is absent, infer the abbreviation from the
    // verse table name (e.g. "KJV_verses" → "KJV").
    private static string DiscoverScrollmapperTranslation(SqliteConnection conn, string fallback)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%_verses' LIMIT 1";
        var tableName = cmd.ExecuteScalar()?.ToString();
        if (tableName is null) return fallback;
        return tableName[..^"_verses".Length];
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ProbeScrollmapperStrongs(SqliteConnection conn, string translation)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT text FROM [{translation}_verses]
                WHERE (book_id = 1 AND chapter = 1 AND verse = 1)
                   OR (book_id = (SELECT MIN(book_id) FROM [{translation}_verses]))
                ORDER BY book_id, chapter, verse
                LIMIT 1
                """;
            var result = cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            return result.Contains("<WH") || result.Contains("<WG");
        }
        catch { return false; }
    }

    private static List<int> GetScrollmapperBookIds(SqliteConnection conn, string translation)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM [{translation}_books] ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ── MySword helpers ───────────────────────────────────────────────────────

    private TranslationInfo? ReadMySwordInfo(string path)
    {
        try
        {
            using var conn = Open(path);
            using var cmd = conn.CreateCommand();

            cmd.CommandText = """
                SELECT Title, Abbreviation, Language, Strong
                FROM   Details
                LIMIT  1
                """;

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var title = r.IsDBNull(0) ? null : r.GetString(0);
            var translation = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            var language = r.IsDBNull(2) ? "en" : r.GetString(2);
            var hasStrongs = !r.IsDBNull(3) && r.GetBoolean(3);

            return new TranslationInfo(translation, title, License: null, hasStrongs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read MySword metadata from '{Path}'", path);
            return null;
        }
    }

    private static List<int> GetMySwordBookIds(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Book FROM Bible ORDER BY Book";
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a verse row from the current reader position.
    /// Column order must be the same for both formats:
    ///   0=book, 1=chapter, 2=verse, 3=text/scripture
    /// </summary>
    private static VerseRecord ReadVerseRecord(
        SqliteDataReader reader, string language, BibleFormat format)
    {
        var b = reader.GetInt32(0);
        var ch = reader.GetInt32(1);
        var v = reader.GetInt32(2);
        var raw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

        var (plainText, strongsWords) = StrongsParser.Parse(raw, format);

        return new VerseRecord(
            Book: b,
            Chapter: ch,
            Verse: v,
            BookName: BookMapper.ToName(b, language),
            Reference: BookMapper.ToReference(b, ch, v, language: language),
            Text: plainText,
            StrongsWords: strongsWords
        );
    }

    private static int ResolveBookOrThrow(string bookName)
    {
        var number = BookMapper.ToNumber(bookName);
        if (number is null)
            throw new ArgumentException($"Unknown book name: '{bookName}'");
        return number.Value;
    }

    /// <summary>
    /// Builds the WHERE clause fragments for testament and book filters,
    /// adding the corresponding SqliteParameters to the command.
    /// Returns an empty string if no filters apply.
    /// </summary>
    private static string BuildTestamentAndBookConditions(
        string bookColumn,
        string testament,
        int? bookNumber,
        SqliteCommand cmd,
        bool isMySword)
    {
        var parts = new List<string>();

        // Testament range
        switch (testament)
        {
            case "OT":
                parts.Add($"{bookColumn} BETWEEN 1 AND 39");
                break;
            case "NT":
                parts.Add($"{bookColumn} BETWEEN 40 AND 66");
                break;
                // "BOTH" → no filter needed
        }

        // Book scope
        if (bookNumber.HasValue)
        {
            parts.Add($"{bookColumn} = $bookNum");
            cmd.Parameters.AddWithValue("$bookNum", bookNumber.Value);
        }

        // For MySword, add the LIKE filter here (no markup in most MySword bibles)
        if (isMySword)
        {
            parts.Add("Scripture LIKE $pattern");
        }

        return parts.Count > 0 ? string.Join(" AND ", parts) : string.Empty;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}