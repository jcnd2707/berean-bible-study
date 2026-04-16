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
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
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

    public List<CommentaryEntry> GetVerse(
        string moduleId, string bookName, int chapter, int verse, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
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

    public CommentaryEntry? GetBookIntro(
        string moduleId, string bookName, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
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

    public CommentaryInfo? GetInfo(string moduleId)
    {
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT Description, Abbreviation, Comments, VersionDate, Language
            FROM   details
            LIMIT  1
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new CommentaryInfo(
            ModuleId: moduleId,
            Title: reader.IsDBNull(0) ? moduleId : reader.GetString(0),
            Abbreviation: reader.IsDBNull(1) ? moduleId : reader.GetString(1),
            Description: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            VersionDate: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Language: reader.IsDBNull(4) ? "en" : reader.GetString(4)
        );
    }

    // ── Private helpers ───────────────────────────────────────────────────────

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

    /// <summary>
    /// Converts MySword HTML commentary text to readable plain text,
    /// preserving paragraph breaks, line breaks, and list bullets.
    /// </summary>
    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = html;

        // 1. Block-level closing tags → paragraph break (two newlines)
        text = Regex.Replace(text, @"</(?:p|h[1-6]|div|blockquote)\s*>", "\n\n", RegexOptions.IgnoreCase);

        // 2. <br> → single newline
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        // 3. <hr> → divider line
        text = Regex.Replace(text, @"<hr\s*/?>", "\n---\n", RegexOptions.IgnoreCase);

        // 4. List items → bullet on its own line
        text = Regex.Replace(text, @"<li\s*>", "\n• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</li\s*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(?:ul|ol)\s*>", "\n", RegexOptions.IgnoreCase);

        // 5. Strip all remaining tags (inline: <b>, <i>, <a>, <span>, <font>, etc.)
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);

        // 6. Decode common HTML entities
        text = text
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#39;", "'")
            .Replace("&#34;", "\"")
            .Replace("&amp;", "&")   // must be last among & patterns
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&nbsp;", " ");

        // 7. Collapse runs of spaces on each line (preserve newlines)
        text = Regex.Replace(text, @"[^\S\n]+", " ");

        // 8. Trim each line, then collapse runs of more than one blank line
        var lines = text
            .Split('\n')
            .Select(l => l.Trim());

        var result = new System.Text.StringBuilder();
        int blankCount = 0;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blankCount++;
                // Allow at most one blank line between paragraphs
                if (blankCount == 1)
                    result.Append('\n');
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