using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace BereanResourceApi.Services;

public class BookService(
    IOptions<BereanResourcesConfig> config,
    ILogger<BookService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    // ── Public API ────────────────────────────────────────────────────────────

    public List<BookSummary> GetAvailableBooks()
    {
        var folder = ResolveFolderPath();
        if (folder is null) return [];

        var results = new List<BookSummary>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.db"))
        {
            var moduleId = Path.GetFileNameWithoutExtension(file);
            var meta = ReadMeta(file, moduleId);
            if (meta is null) continue;
            results.Add(new BookSummary(meta.ModuleId, meta.Title, meta.Author, meta.Publisher, meta.Language));
        }
        return results.OrderBy(b => b.Language).ThenBy(b => b.Title).ToList();
    }

    public BookMeta? GetMeta(string moduleId)
    {
        var path = ResolveOrNull(moduleId);
        return path is null ? null : ReadMeta(path, moduleId);
    }

    public List<BookChapterSummary> GetChapters(string moduleId)
    {
        var path = ResolveOrNull(moduleId);
        if (path is null) return [];

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, chapter_number, title, order_index
            FROM chapters
            WHERE book_id = 1
            ORDER BY order_index
            """;

        var chapters = new List<BookChapterSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chapters.Add(new BookChapterSummary(
                Id: reader.GetInt32(0),
                ChapterNumber: reader.GetInt32(1),
                Title: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                OrderIndex: reader.GetInt32(3)
            ));
        }
        return chapters;
    }

    public BookChapterContent? GetChapter(string moduleId, int chapterId)
    {
        var path = ResolveOrNull(moduleId);
        if (path is null) return null;

        using var conn = Open(path);

        using var chapCmd = conn.CreateCommand();
        chapCmd.CommandText = """
            SELECT id, chapter_number, title
            FROM chapters
            WHERE id = $id AND book_id = 1
            """;
        chapCmd.Parameters.AddWithValue("$id", chapterId);

        int chapNumber;
        string chapTitle;
        using (var chapReader = chapCmd.ExecuteReader())
        {
            if (!chapReader.Read()) return null;
            chapNumber = chapReader.GetInt32(1);
            chapTitle = chapReader.IsDBNull(2) ? string.Empty : chapReader.GetString(2);
        }

        using var paraCmd = conn.CreateCommand();
        paraCmd.CommandText = """
            SELECT order_index, css_class, content
            FROM paragraphs
            WHERE chapter_id = $chapterId
            ORDER BY order_index
            """;
        paraCmd.Parameters.AddWithValue("$chapterId", chapterId);

        var paragraphs = new List<BookParagraph>();
        using var paraReader = paraCmd.ExecuteReader();
        while (paraReader.Read())
        {
            var html = paraReader.IsDBNull(2) ? string.Empty : paraReader.GetString(2);
            paragraphs.Add(new BookParagraph(
                OrderIndex: paraReader.GetInt32(0),
                CssClass: paraReader.IsDBNull(1) ? string.Empty : paraReader.GetString(1),
                Content: html,
                PlainText: HtmlToPlainText(html)
            ));
        }

        return new BookChapterContent(
            ModuleId: moduleId,
            ChapterId: chapterId,
            ChapterNumber: chapNumber,
            ChapterTitle: chapTitle,
            Paragraphs: paragraphs
        );
    }

    public List<BookSearchResult> Search(string moduleId, string query, int limit = 50)
    {
        var path = ResolveOrNull(moduleId);
        if (path is null) return [];

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.title, c.chapter_number, p.order_index, p.css_class, p.content
            FROM paragraphs p
            JOIN chapters c ON p.chapter_id = c.id
            WHERE p.content LIKE $query
            ORDER BY c.order_index, p.order_index
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<BookSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var html = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            results.Add(new BookSearchResult(
                ChapterTitle: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                ChapterNumber: reader.GetInt32(1),
                ParagraphIndex: reader.GetInt32(2),
                CssClass: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Content: html,
                PlainText: HtmlToPlainText(html)
            ));
        }
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private BookMeta? ReadMeta(string path, string moduleId)
    {
        try
        {
            using var conn = Open(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT title, author, publisher, language, isbn, rights, imported_at
                FROM meta
                WHERE id = 1
                """;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new BookMeta(
                ModuleId: moduleId,
                Title: reader.IsDBNull(0) ? moduleId : reader.GetString(0),
                Author: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Publisher: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Language: reader.IsDBNull(3) ? "en" : reader.GetString(3),
                Isbn: reader.IsDBNull(4) ? null : reader.GetString(4),
                Rights: reader.IsDBNull(5) ? null : reader.GetString(5),
                ImportedAt: reader.IsDBNull(6)
                    ? DateTime.MinValue
                    : DateTime.Parse(reader.GetString(6))
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read meta from book '{ModuleId}'.", moduleId);
            return null;
        }
    }

    private string? ResolveOrNull(string moduleId)
    {
        var folder = ResolveFolderPath();
        if (folder is null) return null;
        var path = Path.Combine(folder, moduleId + ".db");
        if (!File.Exists(path))
        {
            logger.LogWarning("Book module '{ModuleId}' not found.", moduleId);
            return null;
        }
        return path;
    }

    private string? ResolveFolderPath()
    {
        var folder = Path.Combine(_cfg.RootPath, _cfg.SubFolders.Books);
        return Directory.Exists(folder) ? folder : null;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

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
        var sb = new System.Text.StringBuilder();
        int blankCount = 0;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blankCount++;
                if (blankCount == 1) sb.Append('\n');
            }
            else
            {
                blankCount = 0;
                sb.Append(line).Append('\n');
            }
        }

        return sb.ToString().Trim();
    }
}
