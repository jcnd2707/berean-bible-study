using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Reads e-Sword module files (.bblx, .cmtx, .dctx, .topx, .devx) directly.
/// All of these are SQLite databases with renamed extensions — no special parser needed.
///
/// Returns plain-text records with verse metadata where available.
/// RTF and HTML markup is stripped before returning so the chunker receives
/// clean text identical to what it would get from a .txt file.
/// </summary>
public static class ESwordReader
{
    // ── Public entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Open an e-Sword file and return all readable records.
    /// File type is detected from the tables present inside the SQLite database.
    /// </summary>
    public static async Task<List<ESwordRecord>> ReadAsync(
        string filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"e-Sword module not found: {filePath}");

        var cs = $"Data Source={filePath};Mode=ReadOnly;";

        using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        var tables = await GetTableNamesAsync(conn, ct);

        if (tables.Contains("Bible", StringComparer.OrdinalIgnoreCase)) return await ReadBibleAsync(conn, filePath, ct);
        if (tables.Contains("Verses", StringComparer.OrdinalIgnoreCase)) return await ReadCommentaryAsync(conn, filePath, ct);
        if (tables.Contains("Dictionary", StringComparer.OrdinalIgnoreCase)) return await ReadDictionaryAsync(conn, filePath, ct);
        if (tables.Contains("Topic", StringComparer.OrdinalIgnoreCase)) return await ReadTopicAsync(conn, filePath, ct);

        throw new InvalidOperationException(
            $"Unrecognised e-Sword module format in '{filePath}'. " +
            $"Tables found: {string.Join(", ", tables)}");
    }

    // ── Bible (.bblx) ──────────────────────────────────────────────────────
    // Table: Bible(Book INT, Chapter INT, Verse INT, Scripture TEXT)

    private static async Task<List<ESwordRecord>> ReadBibleAsync(
        SqliteConnection conn, string source, CancellationToken ct)
    {
        var records = new List<ESwordRecord>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Book, Chapter, Verse, Scripture FROM Bible ORDER BY Book, Chapter, Verse";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var text = StripMarkup(reader.GetString(3));
            if (string.IsNullOrWhiteSpace(text)) continue;

            records.Add(new ESwordRecord
            {
                Source = Path.GetFileName(source),
                Text = text,
                BookNumber = reader.GetInt32(0),
                ChapterBegin = reader.GetInt32(1),
                VerseBegin = reader.GetInt32(2),
                VerseEnd = reader.GetInt32(2),
            });
        }

        return records;
    }

    // ── Commentary (.cmtx) ─────────────────────────────────────────────────
    // Three tables: Books(Book, Comments)
    //               Chapters(Book, ChapterBegin, ChapterEnd, Comments)
    //               Verses(Book, ChapterBegin, ChapterEnd, VerseBegin, VerseEnd, Comments)

    private static async Task<List<ESwordRecord>> ReadCommentaryAsync(
        SqliteConnection conn, string source, CancellationToken ct)
    {
        var records = new List<ESwordRecord>();
        var fname = Path.GetFileName(source);
        var tables = await GetTableNamesAsync(conn, ct);

        // Verse-level comments (most useful for RAG)
        if (tables.Contains("Verses", StringComparer.OrdinalIgnoreCase))
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Book, ChapterBegin, ChapterEnd, VerseBegin, VerseEnd, Comments " +
                "FROM Verses ORDER BY Book, ChapterBegin, VerseBegin";

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var text = StripMarkup(r.GetString(5));
                if (string.IsNullOrWhiteSpace(text)) continue;

                records.Add(new ESwordRecord
                {
                    Source = fname,
                    Text = text,
                    BookNumber = r.GetInt32(0),
                    ChapterBegin = r.GetInt32(1),
                    VerseBegin = r.GetInt32(3),
                    VerseEnd = r.GetInt32(4),
                });
            }
        }

        // Chapter-level comments (good supplementary context)
        if (tables.Contains("Chapters", StringComparer.OrdinalIgnoreCase))
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Book, ChapterBegin, Comments FROM Chapters ORDER BY Book, ChapterBegin";

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var text = StripMarkup(r.GetString(2));
                if (string.IsNullOrWhiteSpace(text)) continue;

                records.Add(new ESwordRecord
                {
                    Source = fname,
                    Text = text,
                    BookNumber = r.GetInt32(0),
                    ChapterBegin = r.GetInt32(1),
                    VerseBegin = null,   // chapter-level, no specific verse
                    VerseEnd = null,
                });
            }
        }

        return records;
    }

    // ── Dictionary (.dctx) ─────────────────────────────────────────────────
    // Table: Dictionary(Topic NVARCHAR, Definition TEXT)

    private static async Task<List<ESwordRecord>> ReadDictionaryAsync(
        SqliteConnection conn, string source, CancellationToken ct)
    {
        var records = new List<ESwordRecord>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Topic, Definition FROM Dictionary ORDER BY Topic";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var topic = reader.GetString(0);
            var def = StripMarkup(reader.GetString(1));
            if (string.IsNullOrWhiteSpace(def)) continue;

            records.Add(new ESwordRecord
            {
                Source = Path.GetFileName(source),
                // Prepend the topic so the model knows what the chunk is about
                Text = $"{topic}: {def}",
            });
        }

        return records;
    }

    // ── Topic / Devotional (.topx, .devx) ─────────────────────────────────
    // Table: Topic(Number INT, Topic NVARCHAR, Description TEXT)
    // (schema varies slightly; fall back gracefully if Description is absent)

    private static async Task<List<ESwordRecord>> ReadTopicAsync(
        SqliteConnection conn, string source, CancellationToken ct)
    {
        var records = new List<ESwordRecord>();

        // Detect whether Description or Body column is used
        var columns = await GetColumnNamesAsync(conn, "Topic", ct);
        var textCol = columns.FirstOrDefault(c =>
            c.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("Body", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("Comments", StringComparison.OrdinalIgnoreCase));

        if (textCol is null) return records;

        var topicCol = columns.FirstOrDefault(c =>
            c.Equals("Topic", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("Title", StringComparison.OrdinalIgnoreCase));

        var select = topicCol is not null
            ? $"SELECT {topicCol}, {textCol} FROM Topic"
            : $"SELECT {textCol} FROM Topic";

        var cmd = conn.CreateCommand();
        cmd.CommandText = select;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var fname = Path.GetFileName(source);

        while (await reader.ReadAsync(ct))
        {
            string text;
            if (topicCol is not null)
            {
                var topic = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var body = reader.IsDBNull(1) ? "" : StripMarkup(reader.GetString(1));
                text = string.IsNullOrWhiteSpace(topic) ? body : $"{topic}: {body}";
            }
            else
            {
                text = reader.IsDBNull(0) ? "" : StripMarkup(reader.GetString(0));
            }

            if (!string.IsNullOrWhiteSpace(text))
                records.Add(new ESwordRecord { Source = fname, Text = text });
        }

        return records;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<List<string>> GetTableNamesAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            names.Add(r.GetString(0));
        return names;
    }

    private static async Task<List<string>> GetColumnNamesAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var cols = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cols.Add(r.GetString(1)); // column 1 = name
        return cols;
    }

    // RTF tags: \word, {\...}, control symbols
    private static readonly Regex _rtf = new(
        @"\{[^}]*\}|\\[a-z]+\d*\s?|\\[^a-z]|\r|\n",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // HTML tags and entities
    private static readonly Regex _html = new(
        @"<[^>]+>|&[a-z]+;|&#\d+;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _ws = new(
        @"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Strip RTF or HTML markup and collapse whitespace.
    /// Handles both v9/10 (RTF) and v11+ (HTML) module formats.
    /// </summary>
    private static string StripMarkup(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Detect format: RTF starts with '{' or '\', HTML contains '<'
        string plain = input.TrimStart().StartsWith('<')
            ? _html.Replace(input, " ")
            : _rtf.Replace(input, " ");

        return _ws.Replace(plain, " ").Trim();
    }
}

/// <summary>
/// A single readable record extracted from an e-Sword module.
/// Passed to DocumentChunker.Chunk() to produce DocumentChunks.
/// </summary>
public class ESwordRecord
{
    public required string Source { get; init; }
    public required string Text { get; init; }
    public int? BookNumber { get; init; }
    public int? ChapterBegin { get; init; }
    public int? VerseBegin { get; init; }
    public int? VerseEnd { get; init; }
}