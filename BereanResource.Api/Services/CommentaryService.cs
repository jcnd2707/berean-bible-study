using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace BereanResourceApi.Services;

public class CommentaryService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<CommentaryService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    // ── Public API ────────────────────────────────────────────────────────────

    public CommentaryChapter? GetChapter(
        string moduleId, string bookName, int chapter, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);

        var volumes = TryResolveGroup(moduleId);
        if (volumes is not null)
        {
            var volumePath = FindVolumeForBook(volumes, bookNumber);
            if (volumePath is null) return null;

            using var conn = Open(volumePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT book, chapter, fromverse, toverse, data
                FROM   commentary
                WHERE  book    = $book
                  AND  chapter = $chapter
                ORDER  BY fromverse
                """;
            cmd.Parameters.AddWithValue("$book", bookNumber);
            cmd.Parameters.AddWithValue("$chapter", chapter);

            var entries = ReadEntries(cmd, language);
            if (entries.Count == 0) return null;

            return new CommentaryChapter(
                ModuleId: moduleId,
                Book: bookNumber,
                BookName: BookMapper.ToName(bookNumber, language),
                Chapter: chapter,
                Entries: entries
            );
        }

        var path = ResolveOrThrow(moduleId);
        using var connS = Open(path);
        using var cmdS = connS.CreateCommand();
        cmdS.CommandText = """
            SELECT book, chapter, fromverse, toverse, data
            FROM   commentary
            WHERE  book    = $book
              AND  chapter = $chapter
            ORDER  BY fromverse
            """;
        cmdS.Parameters.AddWithValue("$book", bookNumber);
        cmdS.Parameters.AddWithValue("$chapter", chapter);

        var singleEntries = ReadEntries(cmdS, language);
        if (singleEntries.Count == 0) return null;

        return new CommentaryChapter(
            ModuleId: moduleId,
            Book: bookNumber,
            BookName: BookMapper.ToName(bookNumber, language),
            Chapter: chapter,
            Entries: singleEntries
        );
    }

    public List<CommentaryEntry> GetVerse(
        string moduleId, string bookName, int chapter, int verse, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);

        var volumes = TryResolveGroup(moduleId);
        if (volumes is not null)
        {
            var volumePath = FindVolumeForBook(volumes, bookNumber);
            if (volumePath is null) return [];

            using var conn = Open(volumePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT book, chapter, fromverse, toverse, data
                FROM   commentary
                WHERE  book    =  $book
                  AND  chapter =  $chapter
                  AND  (fromverse = 0
                        OR (fromverse <= $verse AND toverse >= $verse))
                ORDER  BY fromverse
                """;
            cmd.Parameters.AddWithValue("$book", bookNumber);
            cmd.Parameters.AddWithValue("$chapter", chapter);
            cmd.Parameters.AddWithValue("$verse", verse);
            return ReadEntries(cmd, language);
        }

        var path = ResolveOrThrow(moduleId);
        using var connS = Open(path);
        using var cmdS = connS.CreateCommand();
        cmdS.CommandText = """
            SELECT book, chapter, fromverse, toverse, data
            FROM   commentary
            WHERE  book    =  $book
              AND  chapter =  $chapter
              AND  (fromverse = 0
                    OR (fromverse <= $verse AND toverse >= $verse))
            ORDER  BY fromverse
            """;
        cmdS.Parameters.AddWithValue("$book", bookNumber);
        cmdS.Parameters.AddWithValue("$chapter", chapter);
        cmdS.Parameters.AddWithValue("$verse", verse);
        return ReadEntries(cmdS, language);
    }

    public CommentaryEntry? GetBookIntro(
        string moduleId, string bookName, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);

        var volumes = TryResolveGroup(moduleId);
        if (volumes is not null)
        {
            var volumePath = FindVolumeForBook(volumes, bookNumber);
            if (volumePath is null) return null;

            using var conn = Open(volumePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT book, chapter, fromverse, toverse, data
                FROM   commentary
                WHERE  book    = $book
                  AND  chapter = 0
                LIMIT  1
                """;
            cmd.Parameters.AddWithValue("$book", bookNumber);
            return ReadEntries(cmd, language).FirstOrDefault();
        }

        var path = ResolveOrThrow(moduleId);
        using var connS = Open(path);
        using var cmdS = connS.CreateCommand();
        cmdS.CommandText = """
            SELECT book, chapter, fromverse, toverse, data
            FROM   commentary
            WHERE  book    = $book
              AND  chapter = 0
            LIMIT  1
            """;
        cmdS.Parameters.AddWithValue("$book", bookNumber);
        return ReadEntries(cmdS, language).FirstOrDefault();
    }

    /// <summary>
    /// Returns metadata for a commentary. For grouped commentaries the title
    /// comes from the first volume with the volume marker stripped.
    /// </summary>
    public CommentaryInfo? GetInfo(string moduleId)
    {
        // ── Grouped commentary ────────────────────────────────────────────────
        var volumes = TryResolveGroup(moduleId);
        if (volumes is not null)
        {
            string? groupTitle = null;
            string? groupAbbr = null;
            string language = "en";
            string versionDate = string.Empty;
            var descriptions = new List<string>();

            foreach (var volPath in volumes)
            {
                var raw = ReadRawInfo(volPath);
                if (raw is null) continue;

                language = raw.Language;
                versionDate = raw.VersionDate;

                if (!string.IsNullOrWhiteSpace(raw.Description))
                    descriptions.Add(raw.Description);

                // Derive group title/abbr from the first volume that has them.
                groupTitle ??= ResourceDiscoveryService.DeriveGroupTitle(raw.Title);
                groupAbbr ??= ResourceDiscoveryService.DeriveGroupAbbreviation(raw.Abbreviation);
            }

            if (groupTitle is null) return null;

            return new CommentaryInfo(
                ModuleId: moduleId,
                Title: groupTitle,
                Abbreviation: groupAbbr ?? moduleId,
                Description: string.Join("\n\n", descriptions),
                VersionDate: versionDate,
                Language: language
            );
        }

        // ── Single-file commentary ────────────────────────────────────────────
        var singlePath = ResolveOrThrow(moduleId);
        var single = ReadRawInfo(singlePath);
        if (single is null) return null;

        return new CommentaryInfo(
            ModuleId: moduleId,
            Title: single.Title.Length > 0 ? single.Title : moduleId,
            Abbreviation: single.Abbreviation.Length > 0 ? single.Abbreviation : moduleId,
            Description: single.Description,
            VersionDate: single.VersionDate,
            Language: single.Language
        );
    }

    // ── Group resolution (delegates to ResourceDiscoveryService) ─────────────

    private IReadOnlyList<string>? TryResolveGroup(string moduleId)
    {
        var registry = discovery.GetCommentaryGroupRegistry();
        return registry.TryGetValue(moduleId, out var paths) ? paths : null;
    }

    // ── Volume selection ──────────────────────────────────────────────────────

    private static string? FindVolumeForBook(IReadOnlyList<string> volumes, int bookNumber)
    {
        foreach (var path in volumes)
        {
            if (VolumeContainsBook(path, bookNumber))
                return path;
        }
        return null;
    }

    private static bool VolumeContainsBook(string path, int bookNumber)
    {
        try
        {
            using var conn = Open(path);
            if (!TableExists(conn, "commentary")) return false;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM commentary WHERE book = $book LIMIT 1";
            cmd.Parameters.AddWithValue("$book", bookNumber);
            return Convert.ToInt64(cmd.ExecuteScalar()!) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    // ── Raw DB helpers ────────────────────────────────────────────────────────

    private record RawInfo(
        string Title, string Abbreviation, string Description,
        string VersionDate, string Language);

    private static RawInfo? ReadRawInfo(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Description, Abbreviation, Comments, VersionDate, Language
            FROM   details
            LIMIT  1
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new RawInfo(
            Title: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Abbreviation: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Description: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            VersionDate: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Language: reader.IsDBNull(4) ? "en" : reader.GetString(4)
        );
    }

    private static List<CommentaryEntry> ReadEntries(SqliteCommand cmd, string language)
    {
        using var reader = cmd.ExecuteReader();
        var entries = new List<CommentaryEntry>();

        while (reader.Read())
        {
            var book = reader.GetInt32(0);
            var chapter = reader.GetInt32(1);
            var fromVerse = reader.GetInt32(2);
            var toVerse = reader.GetInt32(3);
            var raw = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            entries.Add(new CommentaryEntry(
                Book: book,
                BookName: BookMapper.ToName(book, language),
                Chapter: chapter,
                VerseBegin: fromVerse,
                VerseEnd: toVerse,
                Reference: BookMapper.ToReference(book, chapter, fromVerse, toVerse, language),
                Marker: fromVerse == 0 ? "intro" : null,
                Text: HtmlToPlainText(raw)
            ));
        }

        return entries;
    }

    private string ResolveOrThrow(string moduleId)
    {
        var path = discovery.ResolvePath(_cfg.SubFolders.Commentaries, moduleId, ".cmt");
        if (path is null)
        {
            logger.LogWarning("Commentary module '{ModuleId}' not found.", moduleId);
            throw new FileNotFoundException($"Commentary module '{moduleId}' not found.");
        }
        return path;
    }

    private static int ResolveBookOrThrow(string bookName)
    {
        var number = BookMapper.ToNumber(bookName);
        if (number is null)
            throw new ArgumentException($"Unknown book name: '{bookName}'", nameof(bookName));
        return number.Value;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = html;

        text = Regex.Replace(text, @"</(?:p|h[1-6]|div|blockquote)\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<hr\s*/?>", "\n---\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li\s*>", "\n• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</li\s*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(?:ul|ol)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);

        text = text
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#39;", "'")
            .Replace("&#34;", "\"")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&nbsp;", " ");

        text = Regex.Replace(text, @"[^\S\n]+", " ");

        var lines = text.Split('\n').Select(l => l.Trim());
        var result = new System.Text.StringBuilder();
        int blankCount = 0;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blankCount++;
                if (blankCount == 1) result.Append('\n');
            }
            else
            {
                blankCount = 0;
                result.Append(line).Append('\n');
            }
        }

        return result.ToString().Trim();
    }
}